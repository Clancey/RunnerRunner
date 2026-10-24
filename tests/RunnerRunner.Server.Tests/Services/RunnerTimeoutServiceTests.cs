using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains;
using RunnerRunner.Server.Services;
using Shiny.DocumentDb;
using RunnerRunner.Server.Tests.TestSupport;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Tests.Services;

public class RunnerTimeoutServiceTests
{
    [Fact]
    public void PrepareLinkedEventRetry_ResetsProvisionedQueuedEvent()
    {
        var now = new TestClock().UtcNow;
        var evt = new WebhookEvent
        {
            Id = "evt-1",
            Action = "queued",
            Status = "provisioned",
            InstanceId = "inst-1",
            ReceivedAt = now.AddMinutes(-2),
            UpdatedAt = now.AddMinutes(-1)
        };

        var changed = RunnerTimeoutService.PrepareLinkedEventRetry(
            evt,
            now,
            "Runner came online but never picked up the queued job");

        Assert.True(changed);
        Assert.Equal("pending", evt.Status);
        Assert.Null(evt.InstanceId);
        Assert.Null(evt.ResolvedAt);
        Assert.NotNull(evt.NextRetryAt);
        Assert.Contains("never picked up", evt.Error);
    }

    [Fact]
    public void PrepareLinkedEventRetry_SkipsInProgressEvent()
    {
        var now = new TestClock().UtcNow;
        var evt = new WebhookEvent
        {
            Id = "evt-2",
            Action = "queued",
            Status = "in_progress",
            InstanceId = "inst-2",
            ReceivedAt = now.AddMinutes(-2),
            UpdatedAt = now.AddMinutes(-1)
        };

        var changed = RunnerTimeoutService.PrepareLinkedEventRetry(
            evt,
            now,
            "Runner failed");

        Assert.False(changed);
        Assert.Equal("in_progress", evt.Status);
        Assert.Equal("inst-2", evt.InstanceId);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("timed_out")]
    [InlineData("rejected")]
    [InlineData("ignored")]
    public void PrepareLinkedEventRetry_SkipsTerminalQueuedEvents(string terminalStatus)
    {
        var clock = new TestClock();
        var evt = new WebhookEvent
        {
            Id = $"evt-{terminalStatus}",
            Action = "queued",
            Status = terminalStatus,
            InstanceId = "inst-terminal",
            ResolvedAt = clock.UtcNow.AddMinutes(-1),
            NextRetryAt = null
        };

        var changed = RunnerTimeoutService.PrepareLinkedEventRetry(
            evt,
            clock.UtcNow,
            "Runner failed after provider already resolved the job");

        Assert.False(changed);
        Assert.Equal(terminalStatus, evt.Status);
        Assert.Equal("inst-terminal", evt.InstanceId);
        Assert.Null(evt.NextRetryAt);
    }

    [Fact]
    public async Task ScanForTimeouts_RequeuesPendingDynamicRunnerAndRemovesInstance()
    {
        var store = TestDocumentStore.Create();
        var hostCommands = new RecordingHostCommandDispatcher();
        using var services = new ServiceCollection()
            .AddSingleton(store)
            .BuildServiceProvider();

        var now = DateTime.UtcNow;
        var host = new Host
        {
            Id = "host-timeout",
            Name = "host-timeout",
            Platform = HostPlatform.Linux,
            AgentStatus = AgentStatus.Online
        };
        var evt = new WebhookEvent
        {
            Id = "evt-timeout",
            Action = "queued",
            Status = "provisioned",
            InstanceId = "inst-timeout",
            JobId = "job-timeout",
            ReceivedAt = now.AddMinutes(-20),
            UpdatedAt = now.AddMinutes(-19)
        };
        var instance = new RunnerInstance
        {
            Id = "inst-timeout",
            HostId = host.Id,
            ProfileId = "profile-timeout",
            RunnerName = "dynamic-runner",
            ProvisioningMode = "dynamic",
            WebhookEventId = evt.Id,
            JobId = evt.JobId,
            Status = RunnerInstanceStatus.Pending,
            CreatedAt = now.AddMinutes(-10),
            DeployedAt = now.AddMinutes(-5),
            ContainerId = "container-timeout"
        };

        await store.Insert(host);
        await store.Insert(evt);
        await store.Insert(instance);

        var service = new RunnerTimeoutService(
            NullLogger<RunnerTimeoutService>.Instance,
            services,
            hostCommands,
            new ConfigurationBuilder().Build());

        await InvokeScanForTimeoutsAsync(service);

        var requeuedEvent = await store.Get<WebhookEvent>(evt.Id);
        Assert.NotNull(requeuedEvent);
        Assert.Equal("pending", requeuedEvent!.Status);
        Assert.Null(requeuedEvent.InstanceId);
        Assert.Null(requeuedEvent.ResolvedAt);
        Assert.Contains("deployment timed out", requeuedEvent.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await store.Get<RunnerInstance>(instance.Id));

        var stop = hostCommands.SingleCommand<StopRunnerCommand>(HostCommandKind.StopRunner);
        Assert.Equal(instance.Id, stop.InstanceId);
        Assert.Equal(instance.ContainerId, stop.InstanceHandle);
    }

    [Theory]
    [InlineData("no_match", 25, true)]
    [InlineData("pending_config", 23, false)]
    [InlineData("completed", 8 * 24, true)]
    [InlineData("timed_out", 6 * 24, false)]
    [InlineData("ignored", 3 * 24, true)]
    [InlineData("rejected", 24, false)]
    [InlineData("provisioned", 8 * 24, true)]
    [InlineData("pending", 30 * 24, false)]
    public void ShouldRemoveWebhookEvent_UsesStatusSpecificRetention(
        string status,
        int ageHours,
        bool expected)
    {
        var now = new TestClock().UtcNow;
        var evt = new WebhookEvent
        {
            Id = $"evt-{status}",
            Action = "queued",
            Status = status,
            ReceivedAt = now.AddHours(-ageHours)
        };

        Assert.Equal(expected, InvokeShouldRemoveWebhookEvent(evt, now));
    }

    private static Task InvokeScanForTimeoutsAsync(RunnerTimeoutService service)
    {
        var method = typeof(RunnerTimeoutService).GetMethod(
            "ScanForTimeoutsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        return (Task)method!.Invoke(service, [CancellationToken.None])!;
    }

    private static bool InvokeShouldRemoveWebhookEvent(WebhookEvent evt, DateTime now)
    {
        var method = typeof(RunnerTimeoutService).GetMethod(
            "ShouldRemoveWebhookEvent",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        return (bool)method!.Invoke(null, [evt, now])!;
    }

    // Regression: run 35545917495. Runner Macos-jit-6d85e2e1 was minted for the iOS job
    // 106183357904, but GitHub handed it to build 106183357880. Because the linked iOS
    // event never reached "in_progress", the pickup timeout recycled the runner 10 minutes
    // in -- killing a build mid-step and deleting its workspace.
    [Fact]
    public void ClassifyLinkedJob_DifferentWhenGitHubAssignedAnotherJob()
    {
        var instance = new RunnerInstance { Id = "inst-1", RunnerName = "Macos-jit-6d85e2e1", JobId = "106183357888" };
        var linkedEvent = new WebhookEvent { Id = "evt-1", JobId = "106183357904", Status = "provisioned" };

        Assert.Equal(
            RunnerTimeoutService.LinkedJobBinding.Different,
            RunnerTimeoutService.ClassifyLinkedJob(instance, linkedEvent));
    }

    [Fact]
    public void ClassifyLinkedJob_SameWhenRunnerStillHoldsThatClaim()
    {
        var instance = new RunnerInstance { Id = "inst-1", RunnerName = "Macos-jit-6d85e2e1", JobId = "106183357904" };
        var linkedEvent = new WebhookEvent { Id = "evt-1", JobId = "106183357904", Status = "provisioned" };

        Assert.Equal(
            RunnerTimeoutService.LinkedJobBinding.Same,
            RunnerTimeoutService.ClassifyLinkedJob(instance, linkedEvent));
    }

    [Fact]
    public void ClassifyLinkedJob_UnknownWhenThereIsNothingToCompare()
    {
        // A released claim must classify as Unknown, never as "idle". Observed in
        // production: the webhook processor cleared Macos-jit-dbd36386's stale claim after
        // GitHub gave job 110937915464 to a different runner, the completion path correctly
        // declined to stop it, and the event-resolved reaper then stopped it anyway seven
        // seconds later because a null JobId could not prove adoption. Absence of a claim is
        // not evidence that the runner is doing nothing.
        var linkedEvent = new WebhookEvent { Id = "evt-1", JobId = "110937915464", Status = "completed" };

        Assert.Equal(
            RunnerTimeoutService.LinkedJobBinding.Unknown,
            RunnerTimeoutService.ClassifyLinkedJob(
                new RunnerInstance { Id = "inst-1", RunnerName = "Macos-jit-dbd36386", JobId = null },
                linkedEvent));
        Assert.Equal(
            RunnerTimeoutService.LinkedJobBinding.Unknown,
            RunnerTimeoutService.ClassifyLinkedJob(
                new RunnerInstance { Id = "inst-2", RunnerName = "Macos-jit-dbd36386", JobId = "" },
                linkedEvent));
        Assert.Equal(
            RunnerTimeoutService.LinkedJobBinding.Unknown,
            RunnerTimeoutService.ClassifyLinkedJob(
                new RunnerInstance { Id = "inst-3", RunnerName = "Macos-jit-dbd36386", JobId = "110937915464" },
                null));
        Assert.Equal(
            RunnerTimeoutService.LinkedJobBinding.Unknown,
            RunnerTimeoutService.ClassifyLinkedJob(
                new RunnerInstance { Id = "inst-4", RunnerName = "Macos-jit-dbd36386", JobId = "110937915464" },
                new WebhookEvent { Id = "evt-2", JobId = "" }));
    }

    [Fact]
    public void ClassifyLinkedJob_PrefersTheRunnerTheProviderActuallyAssigned()
    {
        // The stale claim still names this job because the runner's own in_progress
        // webhook has not arrived yet. Comparing job IDs alone would say "Same" and stop a
        // runner that is mid-build; the assigned runner name settles it.
        var linkedEvent = new WebhookEvent
        {
            Id = "evt-1",
            JobId = "110937915464",
            Status = "completed",
            AssignedRunnerName = "Macos-jit-7e8b7047"
        };

        Assert.Equal(
            RunnerTimeoutService.LinkedJobBinding.Different,
            RunnerTimeoutService.ClassifyLinkedJob(
                new RunnerInstance { Id = "inst-1", RunnerName = "Macos-jit-dbd36386", JobId = "110937915464" },
                linkedEvent));

        Assert.Equal(
            RunnerTimeoutService.LinkedJobBinding.Same,
            RunnerTimeoutService.ClassifyLinkedJob(
                new RunnerInstance { Id = "inst-2", RunnerName = "macos-jit-7E8B7047", JobId = null },
                linkedEvent));
    }

    // Reproduces tower-docker 2026-10-20 12:20:14. With ci.clancey.dev down no webhooks
    // arrived, so recovery runner FashionPact-Linux-jit-383f99c0's linked event for job
    // 118618052121 (already completed at 12:09:43) stayed "provisioned" with no assigned
    // runner, and its claim still matched by job id. GitHub had given the runner #708's
    // build-function job 118556730050 at 12:13:52; the pickup timeout "recycled" it during
    // "Run Integration Tests" and the job's firewall-close step never ran. jit-aa440f7f
    // died the same way at 12:31:14.
    [Fact]
    public async Task PickupTimeout_WithWebhooksDown_LeavesARunnerTheProviderReportsBusy()
    {
        var scenario = await PickupScenarioAsync();
        var registrations = new FakeActivity(ProviderRunnerActivity.Busy);

        await ScanAsync(scenario.Store, scenario.HostCommands, registrations);

        Assert.DoesNotContain(scenario.HostCommands.Commands, c => c.Kind == HostCommandKind.StopRunner);
        var record = await scenario.Store.Get<RunnerInstance>(scenario.Instance.Id);
        Assert.NotNull(record);
        Assert.Equal(RunnerInstanceStatus.Running, record!.Status);
        var evt = await scenario.Store.Get<WebhookEvent>(scenario.Event.Id);
        Assert.Equal("provisioned", evt!.Status);
        Assert.Equal(0, evt.RetryCount);
    }

    [Theory]
    [InlineData(ProviderRunnerActivity.Unknown)]
    [InlineData(ProviderRunnerActivity.Busy)]
    public async Task ResolvedEvent_StillBoundByJobId_LeavesARunnerThatMayBeBuilding(ProviderRunnerActivity activity)
    {
        // Same mechanism as the pickup recycle: once the event resolves, the job-id claim is
        // the only binding evidence, and it does not move when the webhook that would release
        // it is lost. The runner may already be building the job GitHub handed it.
        var scenario = await PickupScenarioAsync(eventStatus: "completed");

        await ScanAsync(scenario.Store, scenario.HostCommands, new FakeActivity(activity));

        Assert.DoesNotContain(scenario.HostCommands.Commands, c => c.Kind == HostCommandKind.StopRunner);
        Assert.Equal(RunnerInstanceStatus.Running, (await scenario.Store.Get<RunnerInstance>(scenario.Instance.Id))!.Status);
    }

    [Fact]
    public async Task PickupTimeout_ProviderReportsIdle_RecyclesWithOneBackendSpecificStop()
    {
        var scenario = await PickupScenarioAsync();

        await ScanAsync(scenario.Store, scenario.HostCommands, new FakeActivity(ProviderRunnerActivity.Idle));

        var stop = scenario.HostCommands.SingleCommand<StopRunnerCommand>(HostCommandKind.StopRunner);
        Assert.Equal(scenario.Instance.ContainerId, stop.InstanceHandle);
        Assert.Equal(ExecutionBackend.Docker, stop.Backend);
        Assert.Null(await scenario.Store.Get<RunnerInstance>(scenario.Instance.Id));
        Assert.Equal("pending", (await scenario.Store.Get<WebhookEvent>(scenario.Event.Id))!.Status);
    }

    [Fact]
    public async Task ProviderActivity_BusyIsCachedBetweenScans()
    {
        var scenario = await PickupScenarioAsync();
        var registrations = new FakeActivity(ProviderRunnerActivity.Busy);
        var service = CreateService(scenario.Store, scenario.HostCommands, registrations, out var services);
        using (services)
        {
            await InvokeScanForTimeoutsAsync(service);
            await InvokeScanForTimeoutsAsync(service);
        }

        Assert.Equal(1, registrations.Calls);
    }

    // Reproduces M4ini 2026-10-20 11:48:19-11:51:16: the host slept, so health reports for
    // native runner Macos-jit-073525a2 (PID 3651, LogicLand job 118606912280) stopped for
    // more than three minutes. The stale-health check marked it Crashed, stopped it and
    // removed its record; the job died with "Process completed with exit code 1".
    [Fact]
    public async Task StaleHealth_KeepsTheRunnerAndSendsNoStop()
    {
        var store = await TestDocumentStore.CreateWithWebhookEventsTableAsync();
        var hostCommands = new RecordingHostCommandDispatcher();
        var now = DateTime.UtcNow;
        await store.Insert(new Host { Id = "M4ini", Name = "M4ini", Platform = HostPlatform.MacOS, AgentStatus = AgentStatus.Offline });
        await store.Insert(new RunnerInstance
        {
            Id = "819c357d",
            HostId = "M4ini",
            RunnerName = "Macos-jit-073525a2",
            ProvisioningMode = "dynamic",
            Status = RunnerInstanceStatus.Running,
            ProcessId = 3651,
            StartedAt = now.AddMinutes(-9),
            LastHealthCheck = now.AddMinutes(-4),
            StatusMessage = "Running"
        });

        await ScanAsync(store, hostCommands, registrations: null);

        Assert.DoesNotContain(hostCommands.Commands, c => c.Kind == HostCommandKind.StopRunner);
        var record = await store.Get<RunnerInstance>("819c357d");
        Assert.NotNull(record);
        Assert.Equal(RunnerInstanceStatus.Running, record!.Status);
        Assert.StartsWith(RunnerInstanceGrain.HealthStaleMessagePrefix, record.StatusMessage);
    }

    [Fact]
    public async Task DynamicRunningBackstop_LeavesARunnerTheProviderReportsBusy()
    {
        var store = await TestDocumentStore.CreateWithWebhookEventsTableAsync();
        var hostCommands = new RecordingHostCommandDispatcher();
        var now = DateTime.UtcNow;
        await store.Insert(new Host { Id = "tower-docker", Name = "tower-docker", Platform = HostPlatform.Linux, AgentStatus = AgentStatus.Online });
        await store.Insert(new RunnerInstance
        {
            Id = "9ac7f9f7",
            HostId = "tower-docker",
            RunnerName = "ll-linux-jit-e38466ec",
            ProvisioningMode = "dynamic",
            Status = RunnerInstanceStatus.Running,
            ContainerId = "f98754d595f4",
            StartedAt = now.AddHours(-7),
            LastHealthCheck = now
        });

        await ScanAsync(store, hostCommands, new FakeActivity(ProviderRunnerActivity.Busy));

        Assert.DoesNotContain(hostCommands.Commands, c => c.Kind == HostCommandKind.StopRunner);
        Assert.Equal(RunnerInstanceStatus.Running, (await store.Get<RunnerInstance>("9ac7f9f7"))!.Status);
    }

    // Reproduces M4ini 2026-10-20: instance 8aefd1a6 was marked "crashed: stopped" at
    // 12:37:36 while Tart VM rr-FashionPact-macOS-VM-jit-b848a12a kept running job
    // 118626348797, which succeeded at 13:00:18. The record kept naming the VM, so it was not
    // an orphan, and reconciliation kept refreshing its health -- which also reset the
    // terminal-record retention clock. Nothing ever stopped the VM; at 13:57 it still held one
    // of M4ini's two macOS VM slots.
    [Fact]
    public async Task TerminalRecordStillRunningOnItsHost_IsStoppedOnceTheProviderNoLongerHasTheRunner()
    {
        var (store, hostCommands) = await LeakedTartScenarioAsync();

        await ScanAsync(store, hostCommands, new FakeActivity(ProviderRunnerActivity.NotRegistered));

        var stop = hostCommands.SingleCommand<StopRunnerCommand>(HostCommandKind.StopRunner);
        Assert.Equal("8aefd1a6-d063-4649-b699-90a4cf65c2ff", stop.InstanceId);
        Assert.Equal("rr-FashionPact-macOS-VM-jit-b848a12a", stop.InstanceHandle);
        Assert.Equal(ExecutionBackend.Tart, stop.Backend);
        Assert.Null(await store.Get<RunnerInstance>("8aefd1a6-d063-4649-b699-90a4cf65c2ff"));
    }

    [Theory]
    [InlineData(ProviderRunnerActivity.Busy)]
    [InlineData(ProviderRunnerActivity.Unknown)]
    public async Task TerminalRecordStillRunningOnItsHost_IsKeptWhileTheRunnerMayBeBuilding(ProviderRunnerActivity activity)
    {
        var (store, hostCommands) = await LeakedTartScenarioAsync();

        await ScanAsync(store, hostCommands, new FakeActivity(activity));

        Assert.DoesNotContain(hostCommands.Commands, c => c.Kind == HostCommandKind.StopRunner);
        Assert.NotNull(await store.Get<RunnerInstance>("8aefd1a6-d063-4649-b699-90a4cf65c2ff"));
    }

    // Unknown (GitHub unreachable, credential broken) must not keep a leaked VM forever, but
    // it must not stop one sooner than the backstop would stop a runner GitHub cannot vouch for.
    [Fact]
    public async Task TerminalRecordStillRunningOnItsHost_ProviderUnknownPastTheCeiling_IsStopped()
    {
        var (store, hostCommands) = await LeakedTartScenarioAsync(crashedMinutesAgo: 13 * 60);

        await ScanAsync(store, hostCommands, new FakeActivity(ProviderRunnerActivity.Unknown));

        var stop = hostCommands.SingleCommand<StopRunnerCommand>(HostCommandKind.StopRunner);
        Assert.Equal("rr-FashionPact-macOS-VM-jit-b848a12a", stop.InstanceHandle);
        Assert.Null(await store.Get<RunnerInstance>("8aefd1a6-d063-4649-b699-90a4cf65c2ff"));
    }

    [Fact]
    public async Task TerminalRecordStillRunningOnItsHost_ProviderBusyPastTheCeiling_IsKept()
    {
        var (store, hostCommands) = await LeakedTartScenarioAsync(crashedMinutesAgo: 13 * 60);

        await ScanAsync(store, hostCommands, new FakeActivity(ProviderRunnerActivity.Busy));

        Assert.DoesNotContain(hostCommands.Commands, c => c.Kind == HostCommandKind.StopRunner);
        Assert.NotNull(await store.Get<RunnerInstance>("8aefd1a6-d063-4649-b699-90a4cf65c2ff"));
    }

    // The 6h backstop used to stop on anything but Busy. Unknown is what a GitHub outage or
    // an expired token returns for every runner at once, so the backstop would kill every
    // long job in the fleet. Unknown now waits for the ceiling (twice the backstop).
    [Fact]
    public async Task DynamicRunningBackstop_ProviderUnknown_LeavesTheRunnerUntilTheCeiling()
    {
        var (store, hostCommands) = await BackstopScenarioAsync(startedHoursAgo: 7);

        await ScanAsync(store, hostCommands, new FakeActivity(ProviderRunnerActivity.Unknown));

        Assert.DoesNotContain(hostCommands.Commands, c => c.Kind == HostCommandKind.StopRunner);
        Assert.Equal(RunnerInstanceStatus.Running, (await store.Get<RunnerInstance>("9ac7f9f7"))!.Status);
    }

    [Fact]
    public async Task DynamicRunningBackstop_ProviderUnknownPastTheCeiling_StopsTheRunner()
    {
        var (store, hostCommands) = await BackstopScenarioAsync(startedHoursAgo: 13);

        await ScanAsync(store, hostCommands, new FakeActivity(ProviderRunnerActivity.Unknown));

        var stop = hostCommands.SingleCommand<StopRunnerCommand>(HostCommandKind.StopRunner);
        Assert.Equal("f98754d595f4", stop.InstanceHandle);
        Assert.Null(await store.Get<RunnerInstance>("9ac7f9f7"));
    }

    // A cached Unknown may only keep a runner. Asked half a minute before the ceiling, the
    // provider answered Unknown, which was cached for two minutes; the next scan, past the
    // ceiling, reused it to stop a runner GitHub had meanwhile reported busy.
    [Fact]
    public async Task DynamicRunningBackstop_UnknownBeforeTheCeilingThenBusy_IsNotStoppedOnTheCachedUnknown()
    {
        var (store, hostCommands) = await BackstopScenarioAsync(startedHoursAgo: 7);
        var registrations = new FakeActivity(ProviderRunnerActivity.Unknown);
        var service = CreateService(store, hostCommands, registrations, out var services);
        using (services)
        {
            await SetStartedAtAsync(store, "9ac7f9f7", DateTime.UtcNow - UnknownCeiling + TimeSpan.FromSeconds(30));
            await InvokeScanForTimeoutsAsync(service);
            Assert.DoesNotContain(hostCommands.Commands, c => c.Kind == HostCommandKind.StopRunner);

            // A minute later: past the ceiling, still inside the cache window.
            await SetStartedAtAsync(store, "9ac7f9f7", DateTime.UtcNow - UnknownCeiling - TimeSpan.FromSeconds(30));
            registrations.Activity = ProviderRunnerActivity.Busy;
            await InvokeScanForTimeoutsAsync(service);
        }

        Assert.DoesNotContain(hostCommands.Commands, c => c.Kind == HostCommandKind.StopRunner);
        Assert.Equal(RunnerInstanceStatus.Running, (await store.Get<RunnerInstance>("9ac7f9f7"))!.Status);
        Assert.Equal(2, registrations.Calls);
    }

    // Guard: past the ceiling a fresh Unknown still stops the runner.
    [Fact]
    public async Task DynamicRunningBackstop_UnknownBeforeTheCeilingAndAgainAfterIt_StopsOnTheFreshAnswer()
    {
        var (store, hostCommands) = await BackstopScenarioAsync(startedHoursAgo: 7);
        var registrations = new FakeActivity(ProviderRunnerActivity.Unknown);
        var service = CreateService(store, hostCommands, registrations, out var services);
        using (services)
        {
            await SetStartedAtAsync(store, "9ac7f9f7", DateTime.UtcNow - UnknownCeiling + TimeSpan.FromSeconds(30));
            await InvokeScanForTimeoutsAsync(service);
            await SetStartedAtAsync(store, "9ac7f9f7", DateTime.UtcNow - UnknownCeiling - TimeSpan.FromSeconds(30));
            await InvokeScanForTimeoutsAsync(service);
        }

        Assert.Equal("f98754d595f4", hostCommands.SingleCommand<StopRunnerCommand>(HostCommandKind.StopRunner).InstanceHandle);
        Assert.Equal(2, registrations.Calls);
    }

    // The terminal-record path has the same ceiling and the same cache.
    [Fact]
    public async Task TerminalRecordStillRunningOnItsHost_UnknownBeforeTheCeilingThenBusy_IsKept()
    {
        const string id = "8aefd1a6-d063-4649-b699-90a4cf65c2ff";
        var (store, hostCommands) = await LeakedTartScenarioAsync();
        var registrations = new FakeActivity(ProviderRunnerActivity.Unknown);
        var service = CreateService(store, hostCommands, registrations, out var services);
        using (services)
        {
            await SetStartedAtAsync(store, id, DateTime.UtcNow - UnknownCeiling + TimeSpan.FromSeconds(30));
            await InvokeScanForTimeoutsAsync(service);
            Assert.DoesNotContain(hostCommands.Commands, c => c.Kind == HostCommandKind.StopRunner);

            await SetStartedAtAsync(store, id, DateTime.UtcNow - UnknownCeiling - TimeSpan.FromSeconds(30));
            registrations.Activity = ProviderRunnerActivity.Busy;
            await InvokeScanForTimeoutsAsync(service);
        }

        Assert.DoesNotContain(hostCommands.Commands, c => c.Kind == HostCommandKind.StopRunner);
        Assert.NotNull(await store.Get<RunnerInstance>(id));
        Assert.Equal(2, registrations.Calls);
    }

    // Twice the default 6h DynamicProvisioning:RunningTimeoutMinutes.
    private static readonly TimeSpan UnknownCeiling = TimeSpan.FromHours(12);

    private static async Task SetStartedAtAsync(IDocumentStore store, string instanceId, DateTime startedAt)
    {
        var record = (await store.Get<RunnerInstance>(instanceId))!;
        record.StartedAt = startedAt;
        await store.Update(record);
    }

    // GrpcHostCommandDispatcher has no outbox: a stop for a host whose stream is down throws.
    // The recycle used to swallow that, mark the runner Failed, requeue its job and forget the
    // record. The runner was still registered and running, so GitHub could hand it a job, and
    // reconciliation would later clean it up as an orphan mid-job.
    [Fact]
    public async Task PickupTimeout_StopCannotBeSent_KeepsTheRecordAndDoesNotRequeue()
    {
        var scenario = await PickupScenarioAsync();
        scenario.HostCommands.SetHostConnected("tower-docker", connected: false);

        await ScanAsync(scenario.Store, scenario.HostCommands, new FakeActivity(ProviderRunnerActivity.Idle));

        var record = await scenario.Store.Get<RunnerInstance>(scenario.Instance.Id);
        Assert.NotNull(record);
        Assert.Equal(RunnerInstanceStatus.Running, record!.Status);
        var evt = await scenario.Store.Get<WebhookEvent>(scenario.Event.Id);
        Assert.Equal("provisioned", evt!.Status);
        Assert.Equal(0, evt.RetryCount);

        // Once the host is back, the next scan recycles it.
        scenario.HostCommands.SetHostConnected("tower-docker", connected: true);
        await ScanAsync(scenario.Store, scenario.HostCommands, new FakeActivity(ProviderRunnerActivity.Idle));

        scenario.HostCommands.SingleCommand<StopRunnerCommand>(HostCommandKind.StopRunner);
        Assert.Null(await scenario.Store.Get<RunnerInstance>(scenario.Instance.Id));
    }

    [Fact]
    public async Task TerminalRecordStillRunningOnItsHost_StopCannotBeSent_KeepsTheRecord()
    {
        var (store, hostCommands) = await LeakedTartScenarioAsync();
        hostCommands.SetHostConnected("M4ini", connected: false);

        await ScanAsync(store, hostCommands, new FakeActivity(ProviderRunnerActivity.NotRegistered));

        Assert.NotNull(await store.Get<RunnerInstance>("8aefd1a6-d063-4649-b699-90a4cf65c2ff"));
    }

    [Fact]
    public async Task TerminalRecordStillRunningOnItsHost_IsKeptWithoutAskingTheProviderDuringRetention()
    {
        var (store, hostCommands) = await LeakedTartScenarioAsync(crashedMinutesAgo: 5);
        var registrations = new FakeActivity(ProviderRunnerActivity.NotRegistered);

        await ScanAsync(store, hostCommands, registrations);

        Assert.DoesNotContain(hostCommands.Commands, c => c.Kind == HostCommandKind.StopRunner);
        Assert.NotNull(await store.Get<RunnerInstance>("8aefd1a6-d063-4649-b699-90a4cf65c2ff"));
        Assert.Equal(0, registrations.Calls);
    }

    // Stale health only means the server has not heard from the host; the VM may still be
    // running a job. Forgetting the record would turn it into an orphan that reconciliation
    // cleans up as soon as the host reports it again.
    [Fact]
    public async Task TerminalRecordWithStaleHealth_ProviderBusy_IsKept()
    {
        var (store, hostCommands) = await LeakedTartScenarioAsync(lastHealthMinutesAgo: 25);

        await ScanAsync(store, hostCommands, new FakeActivity(ProviderRunnerActivity.Busy));

        Assert.DoesNotContain(hostCommands.Commands, c => c.Kind == HostCommandKind.StopRunner);
        Assert.NotNull(await store.Get<RunnerInstance>("8aefd1a6-d063-4649-b699-90a4cf65c2ff"));
    }

    [Fact]
    public async Task TerminalRecordWithStaleHealth_ProviderNotRegistered_IsStoppedAndRemoved()
    {
        var (store, hostCommands) = await LeakedTartScenarioAsync(lastHealthMinutesAgo: 25);

        await ScanAsync(store, hostCommands, new FakeActivity(ProviderRunnerActivity.NotRegistered));

        var stop = hostCommands.SingleCommand<StopRunnerCommand>(HostCommandKind.StopRunner);
        Assert.Equal("rr-FashionPact-macOS-VM-jit-b848a12a", stop.InstanceHandle);
        Assert.Null(await store.Get<RunnerInstance>("8aefd1a6-d063-4649-b699-90a4cf65c2ff"));
    }

    [Fact]
    public async Task TerminalRecordTheHostReportedGone_IsRemovedAfterRetention()
    {
        var (store, hostCommands) = await LeakedTartScenarioAsync(lastHealthMinutesAgo: 25);
        var record = (await store.Get<RunnerInstance>("8aefd1a6-d063-4649-b699-90a4cf65c2ff"))!;
        record.HostReportedGoneAt = DateTime.UtcNow.AddMinutes(-20);
        await store.Update(record);
        var registrations = new FakeActivity(ProviderRunnerActivity.Busy);

        await ScanAsync(store, hostCommands, registrations);

        Assert.DoesNotContain(hostCommands.Commands, c => c.Kind == HostCommandKind.StopRunner);
        Assert.Null(await store.Get<RunnerInstance>("8aefd1a6-d063-4649-b699-90a4cf65c2ff"));
        Assert.Equal(0, registrations.Calls);
    }

    [Fact]
    public async Task PendingTimeout_StopCannotBeSent_LeavesTheRecordPendingAndDoesNotRequeue()
    {
        var scenario = await PickupScenarioAsync();
        scenario.Instance.Status = RunnerInstanceStatus.Pending;
        scenario.Instance.DeployedAt = DateTime.UtcNow.AddMinutes(-5);
        await scenario.Store.Update(scenario.Instance);
        scenario.HostCommands.SetHostConnected("tower-docker", connected: false);

        await ScanAsync(scenario.Store, scenario.HostCommands, new FakeActivity(ProviderRunnerActivity.Idle));

        var record = await scenario.Store.Get<RunnerInstance>(scenario.Instance.Id);
        Assert.NotNull(record);
        Assert.Equal(RunnerInstanceStatus.Pending, record!.Status);
        var evt = await scenario.Store.Get<WebhookEvent>(scenario.Event.Id);
        Assert.Equal("provisioned", evt!.Status);
        Assert.Equal(0, evt.RetryCount);
    }

    [Fact]
    public async Task StartingTimeout_StopCannotBeSent_LeavesTheRecordStartingAndDoesNotRequeue()
    {
        var scenario = await PickupScenarioAsync();
        scenario.Instance.Status = RunnerInstanceStatus.Starting;
        scenario.Instance.StartedAt = DateTime.UtcNow.AddMinutes(-8);
        await scenario.Store.Update(scenario.Instance);
        scenario.HostCommands.SetHostConnected("tower-docker", connected: false);

        await ScanAsync(scenario.Store, scenario.HostCommands, new FakeActivity(ProviderRunnerActivity.Idle));

        var record = await scenario.Store.Get<RunnerInstance>(scenario.Instance.Id);
        Assert.NotNull(record);
        Assert.Equal(RunnerInstanceStatus.Starting, record!.Status);
        var evt = await scenario.Store.Get<WebhookEvent>(scenario.Event.Id);
        Assert.Equal("provisioned", evt!.Status);
        Assert.Equal(0, evt.RetryCount);
    }

    [Fact]
    public async Task StartingTimeout_StopSent_FailsTheRecordAndRequeues()
    {
        var scenario = await PickupScenarioAsync();
        scenario.Instance.Status = RunnerInstanceStatus.Starting;
        scenario.Instance.StartedAt = DateTime.UtcNow.AddMinutes(-8);
        await scenario.Store.Update(scenario.Instance);

        await ScanAsync(scenario.Store, scenario.HostCommands, new FakeActivity(ProviderRunnerActivity.Idle));

        scenario.HostCommands.SingleCommand<StopRunnerCommand>(HostCommandKind.StopRunner);
        Assert.Null(await scenario.Store.Get<RunnerInstance>(scenario.Instance.Id));
        Assert.Equal("pending", (await scenario.Store.Get<WebhookEvent>(scenario.Event.Id))!.Status);
    }

    // A Starting record only says RunnerRunner never heard the runner start. With its
    // RunnerStarted event lost, a runner that registered and took a job stayed Starting, and
    // the Starting timeout stopped it mid-job and requeued the job.
    [Fact]
    public async Task StartingTimeout_ProviderReportsBusy_DoesNotStopFailOrRequeue()
    {
        var scenario = await StartingTimeoutScenarioAsync(deployedAgo: TimeSpan.FromMinutes(8));
        var registrations = new FakeActivity(ProviderRunnerActivity.Busy);

        await ScanAsync(scenario.Store, scenario.HostCommands, registrations);

        Assert.DoesNotContain(scenario.HostCommands.Commands, c => c.Kind == HostCommandKind.StopRunner);
        var record = await scenario.Store.Get<RunnerInstance>(scenario.Instance.Id);
        Assert.NotNull(record);
        Assert.Equal(RunnerInstanceStatus.Starting, record!.Status);
        var evt = await scenario.Store.Get<WebhookEvent>(scenario.Event.Id);
        Assert.Equal("provisioned", evt!.Status);
        Assert.Equal(0, evt.RetryCount);
        Assert.Equal(1, registrations.Calls);
    }

    // Guard: a runner the provider no longer knows is stopped and its job requeued as before.
    [Fact]
    public async Task StartingTimeout_ProviderReportsNotRegistered_StopsTheRunnerAndRequeues()
    {
        var scenario = await StartingTimeoutScenarioAsync(deployedAgo: TimeSpan.FromMinutes(8));

        await ScanAsync(scenario.Store, scenario.HostCommands, new FakeActivity(ProviderRunnerActivity.NotRegistered));

        Assert.Equal("a511f1ea76b5", scenario.HostCommands.SingleCommand<StopRunnerCommand>(HostCommandKind.StopRunner).InstanceHandle);
        Assert.Null(await scenario.Store.Get<RunnerInstance>(scenario.Instance.Id));
        Assert.Equal("pending", (await scenario.Store.Get<WebhookEvent>(scenario.Event.Id))!.Status);
    }

    // Unknown keeps the runner until the Unknown ceiling, like the pickup recycle and the
    // running backstop; past it the stop goes ahead.
    [Fact]
    public async Task StartingTimeout_ProviderReportsUnknown_StopsOnlyPastTheCeiling()
    {
        var early = await StartingTimeoutScenarioAsync(deployedAgo: UnknownCeiling - TimeSpan.FromMinutes(1));

        await ScanAsync(early.Store, early.HostCommands, new FakeActivity(ProviderRunnerActivity.Unknown));

        Assert.DoesNotContain(early.HostCommands.Commands, c => c.Kind == HostCommandKind.StopRunner);
        Assert.Equal(RunnerInstanceStatus.Starting, (await early.Store.Get<RunnerInstance>(early.Instance.Id))!.Status);
        Assert.Equal("provisioned", (await early.Store.Get<WebhookEvent>(early.Event.Id))!.Status);

        var late = await StartingTimeoutScenarioAsync(deployedAgo: UnknownCeiling + TimeSpan.FromMinutes(1));

        await ScanAsync(late.Store, late.HostCommands, new FakeActivity(ProviderRunnerActivity.Unknown));

        late.HostCommands.SingleCommand<StopRunnerCommand>(HostCommandKind.StopRunner);
        Assert.Null(await late.Store.Get<RunnerInstance>(late.Instance.Id));
    }

    // A dynamic runner deployed deployedAgo whose RunnerStarted event never arrived.
    private static async Task<(IDocumentStore Store, RecordingHostCommandDispatcher HostCommands, RunnerInstance Instance, WebhookEvent Event)> StartingTimeoutScenarioAsync(
        TimeSpan deployedAgo)
    {
        var scenario = await PickupScenarioAsync();
        scenario.Instance.Status = RunnerInstanceStatus.Starting;
        scenario.Instance.StartedAt = null;
        scenario.Instance.DeployedAt = DateTime.UtcNow - deployedAgo;
        scenario.Instance.LastHealthCheck = null;
        await scenario.Store.Update(scenario.Instance);
        return scenario;
    }

    [Fact]
    public async Task StopForInstanceWithNoHostRecord_KeepsTheRecordAndDoesNotRequeue()
    {
        var scenario = await PickupScenarioAsync();
        await scenario.Store.Remove<Host>("tower-docker");

        await ScanAsync(scenario.Store, scenario.HostCommands, new FakeActivity(ProviderRunnerActivity.Idle));

        Assert.NotNull(await scenario.Store.Get<RunnerInstance>(scenario.Instance.Id));
        var evt = await scenario.Store.Get<WebhookEvent>(scenario.Event.Id);
        Assert.Equal("provisioned", evt!.Status);
        Assert.Equal(0, evt.RetryCount);
    }

    private static async Task<(IDocumentStore Store, RecordingHostCommandDispatcher HostCommands)> LeakedTartScenarioAsync(
        int crashedMinutesAgo = 20,
        int lastHealthMinutesAgo = 0)
    {
        var store = await TestDocumentStore.CreateWithWebhookEventsTableAsync();
        var hostCommands = new RecordingHostCommandDispatcher();
        var now = DateTime.UtcNow;
        await store.Insert(new Host { Id = "M4ini", Name = "M4ini", Platform = HostPlatform.MacOS, AgentStatus = AgentStatus.Online });
        await store.Insert(new RunnerInstance
        {
            Id = "8aefd1a6-d063-4649-b699-90a4cf65c2ff",
            HostId = "M4ini",
            RunnerName = "FashionPact-macOS-VM-jit-b848a12a",
            VmName = "rr-FashionPact-macOS-VM-jit-b848a12a",
            ProvisioningMode = "dynamic",
            Status = RunnerInstanceStatus.Crashed,
            StatusMessage = "stopped",
            StartedAt = now.AddMinutes(-crashedMinutesAgo - 7),
            LastHealthCheck = now.AddMinutes(-lastHealthMinutesAgo),
            StatusHistory =
            [
                new StatusHistoryEntry { Timestamp = now.AddMinutes(-crashedMinutesAgo - 7), Status = RunnerInstanceStatus.Running },
                new StatusHistoryEntry { Timestamp = now.AddMinutes(-crashedMinutesAgo), Status = RunnerInstanceStatus.Crashed, StatusMessage = "stopped" }
            ]
        });
        return (store, hostCommands);
    }

    private static async Task<(IDocumentStore Store, RecordingHostCommandDispatcher HostCommands)> BackstopScenarioAsync(int startedHoursAgo)
    {
        var store = await TestDocumentStore.CreateWithWebhookEventsTableAsync();
        var hostCommands = new RecordingHostCommandDispatcher();
        var now = DateTime.UtcNow;
        await store.Insert(new Host { Id = "tower-docker", Name = "tower-docker", Platform = HostPlatform.Linux, AgentStatus = AgentStatus.Online });
        await store.Insert(new RunnerInstance
        {
            Id = "9ac7f9f7",
            HostId = "tower-docker",
            RunnerName = "ll-linux-jit-e38466ec",
            ProvisioningMode = "dynamic",
            Status = RunnerInstanceStatus.Running,
            ContainerId = "f98754d595f4",
            StartedAt = now.AddHours(-startedHoursAgo),
            LastHealthCheck = now
        });
        return (store, hostCommands);
    }

    private static async Task<(IDocumentStore Store, RecordingHostCommandDispatcher HostCommands, RunnerInstance Instance, WebhookEvent Event)> PickupScenarioAsync(
        string eventStatus = "provisioned")
    {
        var store = await TestDocumentStore.CreateWithWebhookEventsTableAsync();
        var hostCommands = new RecordingHostCommandDispatcher();
        var now = DateTime.UtcNow;
        var evt = new WebhookEvent
        {
            Id = "evt-118618052121",
            Action = "queued",
            Status = eventStatus,
            JobId = "118618052121",
            InstanceId = "inst-383f99c0",
            ReceivedAt = now.AddMinutes(-60),
            UpdatedAt = now.AddMinutes(-11)
        };
        var instance = new RunnerInstance
        {
            Id = "inst-383f99c0",
            HostId = "tower-docker",
            RunnerName = "FashionPact-Linux-jit-383f99c0",
            ProvisioningMode = "dynamic",
            WebhookEventId = evt.Id,
            JobId = evt.JobId,
            Status = RunnerInstanceStatus.Running,
            ContainerId = "a511f1ea76b5",
            StartedAt = now.AddMinutes(-11),
            LastHealthCheck = now
        };
        await store.Insert(new Host { Id = "tower-docker", Name = "tower-docker", Platform = HostPlatform.Linux, AgentStatus = AgentStatus.Online });
        await store.Insert(evt);
        await store.Insert(instance);
        return (store, hostCommands, instance, evt);
    }

    private static async Task ScanAsync(IDocumentStore store, RecordingHostCommandDispatcher hostCommands, RunnerRegistrationCleanupService? registrations)
    {
        var service = CreateService(store, hostCommands, registrations, out var services);
        using (services)
            await InvokeScanForTimeoutsAsync(service);
    }

    private static RunnerTimeoutService CreateService(
        IDocumentStore store,
        RecordingHostCommandDispatcher hostCommands,
        RunnerRegistrationCleanupService? registrations,
        out ServiceProvider services)
    {
        var collection = new ServiceCollection().AddSingleton(store);
        if (registrations != null)
            collection.AddSingleton(registrations);
        services = collection.BuildServiceProvider();

        return new RunnerTimeoutService(
            NullLogger<RunnerTimeoutService>.Instance,
            services,
            hostCommands,
            new ConfigurationBuilder().Build());
    }

    private sealed class FakeActivity(ProviderRunnerActivity activity)
        : RunnerRegistrationCleanupService([], NullLogger<RunnerRegistrationCleanupService>.Instance)
    {
        public int Calls { get; private set; }

        public ProviderRunnerActivity Activity { get; set; } = activity;

        public override Task<ProviderRunnerActivity> GetProviderRunnerActivityAsync(
            IDocumentStore store,
            RunnerInstance instance,
            CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Activity);
        }
    }
}
