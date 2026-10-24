using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Providers;
using RunnerRunner.Server.Services;
using RunnerRunner.Server.Tests.TestSupport;
using Shiny.DocumentDb;

namespace RunnerRunner.Server.Tests.Services;

public class ReconciliationServiceTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void IsWithinDeployGracePeriod_PendingInstanceUnderTwoMinutes_IsWithinGrace()
    {
        var referenceTime = Now.AddSeconds(-90);

        Assert.True(ReconciliationService.IsWithinDeployGracePeriod(RunnerInstanceStatus.Pending, referenceTime, Now));
    }

    [Fact]
    public void IsWithinDeployGracePeriod_PendingInstanceOverTwoMinutes_IsNotWithinGrace()
    {
        var referenceTime = Now.AddMinutes(-3);

        Assert.False(ReconciliationService.IsWithinDeployGracePeriod(RunnerInstanceStatus.Pending, referenceTime, Now));
    }

    [Fact]
    public void IsWithinDeployGracePeriod_StartingInstanceUnderFiveMinutes_IsWithinGrace()
    {
        // Regression: a native macOS deploy was observed live taking ~3.5 minutes under host
        // load. A Starting instance at that age must still be within grace, or reconciliation
        // spawns a duplicate runner for a deploy that was never actually dead.
        var referenceTime = Now.AddMinutes(-3.5);

        Assert.True(ReconciliationService.IsWithinDeployGracePeriod(RunnerInstanceStatus.Starting, referenceTime, Now));
    }

    [Fact]
    public void IsWithinDeployGracePeriod_StartingInstanceOverFiveMinutes_IsNotWithinGrace()
    {
        var referenceTime = Now.AddMinutes(-6);

        Assert.False(ReconciliationService.IsWithinDeployGracePeriod(RunnerInstanceStatus.Starting, referenceTime, Now));
    }

    [Fact]
    public void IsWithinDeployGracePeriod_RunningInstance_NeverWithinGrace()
    {
        // A runner that was confirmed Running and then vanished from the host report has
        // genuinely crashed — no grace period should apply, regardless of age.
        var referenceTime = Now.AddSeconds(-1);

        Assert.False(ReconciliationService.IsWithinDeployGracePeriod(RunnerInstanceStatus.Running, referenceTime, Now));
    }

    [Fact]
    public void MatchesRunner_ReturnsTrue_ForVmNameMatch()
    {
        var instance = new RunnerInstance
        {
            Id = "inst-1",
            HostId = "host-1",
            ProfileId = "profile-1",
            RunnerName = "macos-test-jit-0a79fbcf",
            VmName = "rr-macos-test-jit-0a79fbcf"
        };

        var discovered = new DiscoveredRunnerInfo
        {
            RunnerName = "macos-test-jit-0a79fbcf",
            VmName = "rr-macos-test-jit-0a79fbcf",
            Backend = ExecutionBackend.Tart,
            Status = "stopped"
        };

        Assert.True(ReconciliationService.MatchesRunner(instance, discovered));
    }

    [Fact]
    public void MatchesRunner_FallsBackToRunnerName()
    {
        var instance = new RunnerInstance
        {
            Id = "inst-2",
            HostId = "host-1",
            ProfileId = "profile-1",
            RunnerName = "macos-test-jit-32c9f714"
        };

        var discovered = new DiscoveredRunnerInfo
        {
            RunnerName = "macos-test-jit-32c9f714",
            Backend = ExecutionBackend.Tart,
            Status = "stopped"
        };

        Assert.True(ReconciliationService.MatchesRunner(instance, discovered));
    }

    [Fact]
    public void IsRunnerStillActive_ReturnsFalse_ForExitedNativeRunner()
    {
        var discovered = new DiscoveredRunnerInfo
        {
            RunnerName = "MacOS-Native-jit-e98a2f6f",
            ProcessId = 12345,
            Backend = ExecutionBackend.Native,
            IsRunning = false,
            Status = "exited"
        };

        Assert.False(ReconciliationService.IsRunnerStillActive(discovered));
    }

    [Fact]
    public void TryPrepareDynamicWebhookRetry_RequeuesProvisionedQueuedEvent()
    {
        var clock = new TestClock();
        var scenario = new RunnerScenarioBuilder(clock)
            .WithIds("dynamic-retry")
            .WithDynamicWebhook(jobId: "job-3")
            .Build();

        var changed = ReconciliationService.TryPrepareDynamicWebhookRetry(
            scenario.Instance,
            scenario.WebhookEvent,
            clock.UtcNow,
            "Runner exited on the host before the queued job started");

        Assert.True(changed);
        Assert.Equal("pending", scenario.WebhookEvent.Status);
        Assert.Null(scenario.WebhookEvent.InstanceId);
    }

    [Fact]
    public void TryPrepareDynamicWebhookRetry_IgnoresStaticRunnerEvenWithLinkedQueuedEvent()
    {
        var clock = new TestClock();
        var scenario = new RunnerScenarioBuilder(clock)
            .WithIds("static-retry")
            .WithDynamicWebhook(jobId: "job-static")
            .Build();
        scenario.Instance.ProvisioningMode = "static";

        var changed = ReconciliationService.TryPrepareDynamicWebhookRetry(
            scenario.Instance,
            scenario.WebhookEvent,
            clock.UtcNow,
            "Static runner should not recycle webhook work");

        Assert.False(changed);
        Assert.Equal("provisioned", scenario.WebhookEvent.Status);
        Assert.Equal(scenario.Instance.Id, scenario.WebhookEvent.InstanceId);
    }

    // Reproduces M4ini 2026-10-20 11:51:22. The instance records for Tart VM
    // rr-FashionPact-macOS-VM-jit-44578a61 (FashionPactPos job 118535576689) and native PID
    // 3651 (LogicLand job 118606912280) had just been rewritten with a blank host, so the
    // report's live resources matched no record on M4ini. Reconciliation cleaned both up on
    // the first report that listed them, "running: True, status: running", and both jobs
    // died. The same happened on tower-docker after the pickup recycle removed records of
    // containers it had only just asked to stop.
    [Theory]
    [InlineData(ExecutionBackend.Tart)]
    [InlineData(ExecutionBackend.Native)]
    [InlineData(ExecutionBackend.Docker)]
    public async Task RunningOrphan_IsNotCleanedUpUntilItHasBeenReportedForTheGracePeriod(ExecutionBackend backend)
    {
        var (service, dispatcher, store, _) = await CreateReconciliationAsync("M4ini");
        var cleanup = new FakeOrphanActivity(ProviderRunnerActivity.NotRegistered);
        var report = Report("M4ini", RunningResource(backend));

        await service.ProcessReportAsync(report, store, cleanup, Now);
        await service.ProcessReportAsync(report, store, cleanup, Now.AddMinutes(9));

        Assert.DoesNotContain(dispatcher.Commands, c => c.Kind == HostCommandKind.CleanupOrphan);

        await service.ProcessReportAsync(report, store, cleanup, Now + ReconciliationService.RunningOrphanGracePeriod);

        var command = dispatcher.SingleCommand<CleanupOrphanCommand>(HostCommandKind.CleanupOrphan);
        Assert.Equal(backend, command.Backend);
        switch (backend)
        {
            case ExecutionBackend.Tart:
                Assert.Equal("rr-FashionPact-macOS-VM-jit-44578a61", command.VmName);
                Assert.Null(command.ContainerId);
                break;
            case ExecutionBackend.Native:
                Assert.Equal(3651, command.ProcessId);
                Assert.Null(command.ContainerId);
                break;
            case ExecutionBackend.Docker:
                Assert.Equal("f98754d595f4032f0ee1d4dbcd8925cc027681ccaa4fad38a5a663532416c6fb", command.ContainerId);
                break;
        }
    }

    // No local in_progress event is not evidence the runner is idle: the webhook may have been
    // lost. A running orphan is cleaned up only once the provider says it is not working.
    [Fact]
    public async Task RunningOrphan_ProviderBusy_IsNotCleanedUp()
    {
        var (service, dispatcher, store, _) = await CreateReconciliationAsync("M4ini");
        var cleanup = new FakeOrphanActivity(ProviderRunnerActivity.Busy);
        var report = Report("M4ini", RunningResource(ExecutionBackend.Native));

        await service.ProcessReportAsync(report, store, cleanup, Now);
        await service.ProcessReportAsync(report, store, cleanup, Now + ReconciliationService.RunningOrphanGracePeriod);
        await service.ProcessReportAsync(report, store, cleanup, Now.AddHours(13));

        Assert.DoesNotContain(dispatcher.Commands, c => c.Kind == HostCommandKind.CleanupOrphan);
        Assert.True(cleanup.Calls > 0);
    }

    // A running orphan the provider reports busy is in every 30s report; its answer is reused
    // for a couple of minutes, as for a tracked runner, rather than asked for each time.
    [Fact]
    public async Task RunningOrphan_ProviderBusy_IsProbedAgainOnlyAfterTheCacheExpires()
    {
        var (service, dispatcher, store, _) = await CreateReconciliationAsync("M4ini");
        var cleanup = new FakeOrphanActivity(ProviderRunnerActivity.Busy);
        var report = Report("M4ini", RunningResource(ExecutionBackend.Native));
        var pastGrace = Now + ReconciliationService.RunningOrphanGracePeriod;

        await service.ProcessReportAsync(report, store, cleanup, Now);
        await service.ProcessReportAsync(report, store, cleanup, pastGrace);
        await service.ProcessReportAsync(report, store, cleanup, pastGrace.AddSeconds(30));
        await service.ProcessReportAsync(report, store, cleanup, pastGrace.AddSeconds(90));

        Assert.Equal(1, cleanup.Calls);

        cleanup.Activity = ProviderRunnerActivity.Idle;
        await service.ProcessReportAsync(report, store, cleanup, pastGrace.AddMinutes(3));

        Assert.Equal(2, cleanup.Calls);
        Assert.Equal(3651, dispatcher.SingleCommand<CleanupOrphanCommand>(HostCommandKind.CleanupOrphan).ProcessId);
    }

    [Fact]
    public async Task RunningOrphan_ProviderUnknown_IsKeptUntilTheCeilingFromFirstSeen()
    {
        var (service, dispatcher, store, _) = await CreateReconciliationAsync("M4ini");
        var cleanup = new FakeOrphanActivity(ProviderRunnerActivity.Unknown);
        var report = Report("M4ini", RunningResource(ExecutionBackend.Tart));

        await service.ProcessReportAsync(report, store, cleanup, Now);
        await service.ProcessReportAsync(report, store, cleanup, Now + ReconciliationService.RunningOrphanGracePeriod);
        await service.ProcessReportAsync(report, store, cleanup, Now.AddHours(11));

        Assert.DoesNotContain(dispatcher.Commands, c => c.Kind == HostCommandKind.CleanupOrphan);

        await service.ProcessReportAsync(report, store, cleanup, Now.AddHours(12).AddMinutes(1));

        var command = dispatcher.SingleCommand<CleanupOrphanCommand>(HostCommandKind.CleanupOrphan);
        Assert.Equal("rr-FashionPact-macOS-VM-jit-44578a61", command.VmName);
    }

    [Fact]
    public async Task RunningOrphan_NoProfileResolvable_IsKept()
    {
        var (service, dispatcher, store, cleanup) = await CreateReconciliationAsync("M4ini");
        // A profile whose name does not prefix the orphan's runner name: nothing to ask.
        await store.Insert(new RunnerProfile { Id = "win-build", Name = "Windows-Build", Provider = RunnerProvider.GitHubActions });
        Assert.Null(await cleanup.GetOrphanRunnerActivityAsync(store, RunningResource(ExecutionBackend.Docker)));
        var report = Report("M4ini", RunningResource(ExecutionBackend.Docker));

        await service.ProcessReportAsync(report, store, cleanup, Now);
        await service.ProcessReportAsync(report, store, cleanup, Now + ReconciliationService.RunningOrphanGracePeriod);
        await service.ProcessReportAsync(report, store, cleanup, Now.AddHours(13));

        Assert.DoesNotContain(dispatcher.Commands, c => c.Kind == HostCommandKind.CleanupOrphan);
    }

    [Fact]
    public async Task RunningOrphan_WhoseRunnerIsOnAnInProgressJob_IsNeverCleanedUp()
    {
        var (service, dispatcher, store, cleanup) = await CreateReconciliationAsync("M4ini");
        await store.Insert(new WebhookEvent
        {
            Id = "evt-logicland",
            Action = "in_progress",
            Status = "in_progress",
            JobId = "118606912280",
            AssignedRunnerName = "Macos-jit-073525a2"
        });
        var report = Report("M4ini", RunningResource(ExecutionBackend.Native));

        await service.ProcessReportAsync(report, store, cleanup, Now);
        await service.ProcessReportAsync(report, store, cleanup, Now.AddMinutes(10));
        await service.ProcessReportAsync(report, store, cleanup, Now.AddMinutes(30));

        Assert.DoesNotContain(dispatcher.Commands, c => c.Kind == HostCommandKind.CleanupOrphan);
    }

    [Fact]
    public async Task RunningOrphan_IsKeptWhenItsJobCannotBeLookedUp()
    {
        var (service, dispatcher, store, cleanup) = await CreateReconciliationAsync("M4ini", withWebhookEventsTable: false);
        var report = Report("M4ini", RunningResource(ExecutionBackend.Native));

        await service.ProcessReportAsync(report, store, cleanup, Now);
        await service.ProcessReportAsync(report, store, cleanup, Now + ReconciliationService.RunningOrphanGracePeriod);

        Assert.DoesNotContain(dispatcher.Commands, c => c.Kind == HostCommandKind.CleanupOrphan);
    }

    [Fact]
    public async Task RunningOrphan_GraceRestartsAfterTheHostStopsReportingIt()
    {
        var (service, dispatcher, store, cleanup) = await CreateReconciliationAsync("M4ini");
        var running = Report("M4ini", RunningResource(ExecutionBackend.Tart));

        await service.ProcessReportAsync(running, store, cleanup, Now);
        await service.ProcessReportAsync(Report("M4ini"), store, cleanup, Now.AddMinutes(5));
        await service.ProcessReportAsync(running, store, cleanup, Now.AddMinutes(11));

        Assert.DoesNotContain(dispatcher.Commands, c => c.Kind == HostCommandKind.CleanupOrphan);
    }

    // Reproduces M4ini 2026-10-20 11:46:56: "Sending cleanup for orphaned Native resource  on
    // host M4ini (running: False, status: exited)". The HostWorker cleaned up PID 3542, but
    // the server log named no resource because DiscoveredRunnerInfo.ContainerId is "" rather
    // than null for non-Docker resources, so `ContainerId ?? VmName ?? ProcessId` stopped at "".
    [Fact]
    public async Task ExitedOrphan_IsCleanedUpImmediatelyAndTheLogNamesTheResource()
    {
        var logger = new CapturingLogger<ReconciliationService>();
        var (service, dispatcher, store, cleanup) = await CreateReconciliationAsync("M4ini", logger);
        var report = Report("M4ini", new DiscoveredRunnerInfo
        {
            RunnerName = "Macos-jit-2cdc03c7",
            ProcessId = 3542,
            InstanceDir = "/Users/clancey/.runnerrunner/instances/2cdc03c7",
            Backend = ExecutionBackend.Native,
            IsRunning = false,
            Status = "exited"
        });

        await service.ProcessReportAsync(report, store, cleanup, Now);

        var command = dispatcher.SingleCommand<CleanupOrphanCommand>(HostCommandKind.CleanupOrphan);
        Assert.Equal(3542, command.ProcessId);
        Assert.Equal("/Users/clancey/.runnerrunner/instances/2cdc03c7", command.InstanceDir);
        Assert.Null(command.ContainerId);
        Assert.Contains(logger.Messages, m => m.Contains("orphaned Native resource 3542 on host M4ini", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunningResourceOfATerminalRecord_IsNotAnOrphanAndRefreshesItsHealth()
    {
        // Instance 8aefd1a6 was marked "crashed: stopped" at 12:37:36 while its VM kept running
        // job 118626348797. The record still names the VM, so the VM is not an orphan; the
        // refreshed health is what lets RunnerTimeoutService see the host still runs it.
        var (service, dispatcher, store, cleanup) = await CreateReconciliationAsync("M4ini");
        await store.Insert(new RunnerInstance
        {
            Id = "8aefd1a6-d063-4649-b699-90a4cf65c2ff",
            HostId = "M4ini",
            RunnerName = "FashionPact-macOS-VM-jit-b848a12a",
            VmName = "rr-FashionPact-macOS-VM-jit-b848a12a",
            ProvisioningMode = "dynamic",
            Status = RunnerInstanceStatus.Crashed,
            StatusMessage = "stopped"
        });
        var report = Report("M4ini", new DiscoveredRunnerInfo
        {
            RunnerName = "FashionPact-macOS-VM-jit-b848a12a",
            VmName = "rr-FashionPact-macOS-VM-jit-b848a12a",
            Backend = ExecutionBackend.Tart,
            IsRunning = true,
            Status = "running"
        });

        await service.ProcessReportAsync(report, store, cleanup, Now.AddMinutes(30));

        Assert.DoesNotContain(dispatcher.Commands, c => c.Kind == HostCommandKind.CleanupOrphan);
        var record = await store.Get<RunnerInstance>("8aefd1a6-d063-4649-b699-90a4cf65c2ff");
        Assert.Equal(Now.AddMinutes(30), record!.LastHealthCheck);
        Assert.Equal(RunnerInstanceStatus.Crashed, record.Status);
    }

    // A report built while `tart list` failed lists no VMs. The server read every running Tart
    // instance on the host as gone: it marked it crashed, requeued its job and deregistered
    // its runner while the VM kept building. The report now says Tart could not be listed.
    [Fact]
    public async Task RunnerMissingFromAReport_IsNotEndedWhenItsBackendCouldNotBeListed()
    {
        var (service, _, store, cleanup) = await CreateReconciliationAsync("M4ini");
        await store.Insert(new RunnerInstance
        {
            Id = "8aefd1a6-d063-4649-b699-90a4cf65c2ff",
            HostId = "M4ini",
            RunnerName = "FashionPact-macOS-VM-jit-b848a12a",
            VmName = "rr-FashionPact-macOS-VM-jit-b848a12a",
            ProvisioningMode = "dynamic",
            Status = RunnerInstanceStatus.Running,
            DeployedAt = Now.AddMinutes(-9),
            StartedAt = Now.AddMinutes(-7)
        });
        await store.Insert(new RunnerInstance
        {
            Id = "516c3058",
            HostId = "M4ini",
            RunnerName = "Macos-jit-516c3058",
            ProcessId = 3331,
            ProvisioningMode = "dynamic",
            Status = RunnerInstanceStatus.Running,
            DeployedAt = Now.AddMinutes(-9),
            StartedAt = Now.AddMinutes(-7)
        });
        var report = Report("M4ini");
        report.UnavailableBackends = [ExecutionBackend.Tart];

        await service.ProcessReportAsync(report, store, cleanup, Now);

        var vm = await store.Get<RunnerInstance>("8aefd1a6-d063-4649-b699-90a4cf65c2ff");
        Assert.Equal(RunnerInstanceStatus.Running, vm!.Status);
        Assert.Null(vm.StoppedAt);

        // Native processes were listed, and this one really is gone.
        var native = await store.Get<RunnerInstance>("516c3058");
        Assert.Equal(RunnerInstanceStatus.Crashed, native!.Status);
    }

    // An old HostWorker sends no UnavailableBackends, so a report built while `tart list`
    // failed looks authoritative. Without the capability flag a missing runner is unknown.
    [Fact]
    public async Task RunnerMissingFromALegacyReport_IsNotEnded()
    {
        var (service, _, store, cleanup) = await CreateReconciliationAsync("M4ini");
        await store.Insert(new RunnerInstance
        {
            Id = "8aefd1a6-d063-4649-b699-90a4cf65c2ff",
            HostId = "M4ini",
            RunnerName = "FashionPact-macOS-VM-jit-b848a12a",
            VmName = "rr-FashionPact-macOS-VM-jit-b848a12a",
            ProvisioningMode = "dynamic",
            Status = RunnerInstanceStatus.Running,
            DeployedAt = Now.AddMinutes(-9),
            StartedAt = Now.AddMinutes(-7)
        });
        await store.Insert(new RunnerInstance
        {
            Id = "c0ffee01",
            HostId = "M4ini",
            RunnerName = "FashionPact-macOS-VM-jit-c0ffee01",
            VmName = "rr-FashionPact-macOS-VM-jit-c0ffee01",
            ProvisioningMode = "dynamic",
            Status = RunnerInstanceStatus.Crashed,
            StoppedAt = Now.AddMinutes(-20)
        });
        var report = Report("M4ini");
        report.ReportsBackendAvailability = false;

        await service.ProcessReportAsync(report, store, cleanup, Now);

        var vm = await store.Get<RunnerInstance>("8aefd1a6-d063-4649-b699-90a4cf65c2ff");
        Assert.Equal(RunnerInstanceStatus.Running, vm!.Status);
        Assert.Null(vm.StoppedAt);
        Assert.Null((await store.Get<RunnerInstance>("c0ffee01"))!.HostReportedGoneAt);
    }

    [Fact]
    public async Task TerminalRecordMissingFromAReport_IsMarkedHostReportedGone()
    {
        var (service, _, store, cleanup) = await CreateReconciliationAsync("M4ini");
        await store.Insert(new RunnerInstance
        {
            Id = "c0ffee01",
            HostId = "M4ini",
            RunnerName = "FashionPact-macOS-VM-jit-c0ffee01",
            VmName = "rr-FashionPact-macOS-VM-jit-c0ffee01",
            ProvisioningMode = "dynamic",
            Status = RunnerInstanceStatus.Crashed,
            StoppedAt = Now.AddMinutes(-20),
            LastHealthCheck = Now.AddMinutes(-25)
        });

        await service.ProcessReportAsync(Report("M4ini"), store, cleanup, Now);

        var record = await store.Get<RunnerInstance>("c0ffee01");
        Assert.Equal(Now, record!.HostReportedGoneAt);
        Assert.True(RunnerTimeoutService.HostReportedGone(record));
    }

    // Reproduces the native-runner kill: a host whose native runners live under a custom
    // RunnerBasePath reported none of them, with no backend flagged unavailable. Reconciliation
    // marked the live, busy runner crashed, requeued its job and deregistered it mid-job. A
    // runner GitHub reports busy is evidently alive, whatever the report says.
    [Fact]
    public async Task RunnerMissingFromAReport_ProviderBusy_IsLeftRunningAndNotDeregistered()
    {
        var logger = new CapturingLogger<ReconciliationService>();
        var (service, store, cleanup, api) = await ProviderScenarioAsync(
            api => api.RespondJson(IsRepoLookup, RunnersJson((LiveNativeRunner, true))),
            logger);

        await service.ProcessReportAsync(Report("M4ini"), store, cleanup, Now);

        var record = await store.Get<RunnerInstance>(LiveNativeInstanceId);
        Assert.Equal(RunnerInstanceStatus.Running, record!.Status);
        Assert.Null(record.StoppedAt);
        Assert.Null(record.HostReportedGoneAt);
        Assert.Equal("provisioned", (await store.Get<WebhookEvent>(LiveNativeEventId))!.Status);
        Assert.DoesNotContain(api.Requests, IsDeregistration);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("busy", StringComparison.OrdinalIgnoreCase));
    }

    // The busy answer is reused for a while, so a runner left running is not probed on every
    // 30s report; once the provider stops reporting it busy, the next report ends it.
    [Fact]
    public async Task RunnerMissingFromAReport_ProviderBusy_IsProbedAgainOnlyAfterTheCacheExpires()
    {
        var busy = true;
        var (service, store, cleanup, api) = await ProviderScenarioAsync(api => api
            .Respond(IsRepoLookup, _ => FakeProviderHttpApi.JsonResponse(RunnersJson((LiveNativeRunner, busy))))
            .Respond(IsRunnerDelete, _ => new HttpResponseMessage(HttpStatusCode.NoContent)));

        await service.ProcessReportAsync(Report("M4ini"), store, cleanup, Now);
        await service.ProcessReportAsync(Report("M4ini"), store, cleanup, Now.AddSeconds(30));
        await service.ProcessReportAsync(Report("M4ini"), store, cleanup, Now.AddSeconds(90));

        Assert.Single(api.Requests, IsActivityProbe);
        Assert.Equal(RunnerInstanceStatus.Running, (await store.Get<RunnerInstance>(LiveNativeInstanceId))!.Status);

        busy = false;
        await service.ProcessReportAsync(Report("M4ini"), store, cleanup, Now.AddMinutes(3));

        Assert.Equal(2, api.Requests.Count(IsActivityProbe));
        var record = await store.Get<RunnerInstance>(LiveNativeInstanceId);
        Assert.Equal(RunnerInstanceStatus.Crashed, record!.Status);
        Assert.Equal(Now.AddMinutes(3), record.HostReportedGoneAt);
        Assert.Contains(api.Requests, r => r.Method == HttpMethod.Delete);
    }

    // The provider cannot say (a GitHub outage, a credential problem). The host listed the
    // backend and the runner is not there, so the record ends, but the registration stays:
    // deregistering a runner that may be on a job kills that job.
    [Fact]
    public async Task RunnerMissingFromAReport_ProviderUnknown_IsEndedButNotDeregistered()
    {
        var logger = new CapturingLogger<ReconciliationService>();
        var (service, store, cleanup, api) = await ProviderScenarioAsync(
            api => api.RespondJson(IsRepoLookup, """{"message":"boom"}""", HttpStatusCode.InternalServerError),
            logger);

        await service.ProcessReportAsync(Report("M4ini"), store, cleanup, Now);

        var record = await store.Get<RunnerInstance>(LiveNativeInstanceId);
        Assert.Equal(RunnerInstanceStatus.Crashed, record!.Status);
        Assert.Equal(Now, record.HostReportedGoneAt);
        Assert.DoesNotContain(api.Requests, IsDeregistration);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("Not deregistering", StringComparison.Ordinal));
    }

    // Guard: a provider that says the runner is idle or gone keeps the existing behavior.
    [Fact]
    public async Task RunnerMissingFromAReport_ProviderIdle_IsEndedRequeuedAndDeregistered()
    {
        var (service, store, cleanup, api) = await ProviderScenarioAsync(api => api
            .RespondJson(IsRepoLookup, RunnersJson((LiveNativeRunner, false)))
            .Respond(IsRunnerDelete, _ => new HttpResponseMessage(HttpStatusCode.NoContent)));

        await service.ProcessReportAsync(Report("M4ini"), store, cleanup, Now);

        var record = await store.Get<RunnerInstance>(LiveNativeInstanceId);
        Assert.Equal(RunnerInstanceStatus.Crashed, record!.Status);
        Assert.Equal(Now, record.HostReportedGoneAt);
        Assert.Equal("pending", (await store.Get<WebhookEvent>(LiveNativeEventId))!.Status);
        Assert.Contains(api.Requests, r => r.Method == HttpMethod.Delete
            && r.PathAndQuery == "/repos/Clancey/FashionPactPos/actions/runners/1");
    }

    [Fact]
    public async Task RunnerMissingFromAReport_ProviderNotRegistered_IsEndedAndDeregistrationIsAttempted()
    {
        var (service, store, cleanup, api) = await ProviderScenarioAsync(api => api
            .RespondJson(IsRepoLookup, RunnersJson())
            .RespondJson(r => r.RequestUri!.AbsolutePath == "/orgs/Clancey/actions/runners", """{"message":"Not Found"}""", HttpStatusCode.NotFound)
            .RespondJson(r => r.RequestUri!.AbsolutePath == "/users/Clancey", """{"login":"Clancey","type":"User"}"""));

        await service.ProcessReportAsync(Report("M4ini"), store, cleanup, Now);

        var record = await store.Get<RunnerInstance>(LiveNativeInstanceId);
        Assert.Equal(RunnerInstanceStatus.Crashed, record!.Status);
        Assert.Equal("pending", (await store.Get<WebhookEvent>(LiveNativeEventId))!.Status);
        Assert.Contains(api.Requests, IsDeregistration);
    }

    // The host says the process exited, but GitHub says the runner is on a job: one of the
    // two is wrong, and acting on the host's word would requeue the job, clean up the
    // resource and deregister the runner mid-job. Nothing is ended until the provider agrees.
    [Fact]
    public async Task RunnerReportedExited_ProviderBusy_IsLeftRunningWithNoCleanupRequeueOrDeregistration()
    {
        var logger = new CapturingLogger<ReconciliationService>();
        var (service, dispatcher, store, cleanup, api) = await ExitedScenarioAsync(
            api => api.RespondJson(IsRepoLookup, RunnersJson((LiveNativeRunner, true))),
            logger);

        await service.ProcessReportAsync(Report("M4ini", ExitedLiveNativeRunner()), store, cleanup, Now);

        var record = await store.Get<RunnerInstance>(LiveNativeInstanceId);
        Assert.Equal(RunnerInstanceStatus.Running, record!.Status);
        Assert.Null(record.StoppedAt);
        Assert.Null(record.HostReportedGoneAt);
        Assert.Equal("provisioned", (await store.Get<WebhookEvent>(LiveNativeEventId))!.Status);
        Assert.DoesNotContain(dispatcher.Commands, c => c.Kind == HostCommandKind.CleanupOrphan);
        Assert.DoesNotContain(api.Requests, IsDeregistration);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("exited", StringComparison.Ordinal)
            && e.Message.Contains("busy", StringComparison.Ordinal));
    }

    // The probe saw the runner idle, then GitHub assigned it a job before the deregistration
    // DELETE. The listing RemoveRunnerAsync takes right before deleting shows it busy, so the
    // registration (and the job on it) survives; the record still ends, as the probe allowed.
    [Fact]
    public async Task RunnerReportedExited_ProviderIdleThenBusyAtDeregistration_IsNotDeleted()
    {
        var (service, _, store, cleanup, api) = await ExitedScenarioAsync(api => api
            .RespondJson(r => IsRepoLookup(r) && r.RequestUri!.Query.StartsWith("?name=", StringComparison.Ordinal),
                RunnersJson((LiveNativeRunner, false)))
            .RespondJson(r => IsRepoLookup(r) && r.RequestUri!.Query == "?per_page=100",
                RunnersJson((LiveNativeRunner, true)))
            .Respond(IsRunnerDelete, _ => new HttpResponseMessage(HttpStatusCode.NoContent)));

        await service.ProcessReportAsync(Report("M4ini", ExitedLiveNativeRunner()), store, cleanup, Now);

        Assert.Equal(RunnerInstanceStatus.Stopped, (await store.Get<RunnerInstance>(LiveNativeInstanceId))!.Status);
        Assert.Contains(api.Requests, r => r.PathAndQuery == "/repos/Clancey/FashionPactPos/actions/runners?per_page=100");
        Assert.DoesNotContain(api.Requests, r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task RunnerReportedExited_ProviderUnknown_IsStoppedButNotDeregistered()
    {
        var (service, dispatcher, store, cleanup, api) = await ExitedScenarioAsync(
            api => api.RespondJson(IsRepoLookup, """{"message":"boom"}""", HttpStatusCode.InternalServerError));

        await service.ProcessReportAsync(Report("M4ini", ExitedLiveNativeRunner()), store, cleanup, Now);

        Assert.Equal(RunnerInstanceStatus.Stopped, (await store.Get<RunnerInstance>(LiveNativeInstanceId))!.Status);
        Assert.Equal("pending", (await store.Get<WebhookEvent>(LiveNativeEventId))!.Status);
        Assert.Equal(3331, dispatcher.SingleCommand<CleanupOrphanCommand>(HostCommandKind.CleanupOrphan).ProcessId);
        Assert.DoesNotContain(api.Requests, IsDeregistration);
    }

    [Fact]
    public async Task RunnerReportedExited_ProviderIdle_IsStoppedAndDeregistered()
    {
        var (service, _, store, cleanup, api) = await ExitedScenarioAsync(api => api
            .RespondJson(IsRepoLookup, RunnersJson((LiveNativeRunner, false)))
            .Respond(IsRunnerDelete, _ => new HttpResponseMessage(HttpStatusCode.NoContent)));

        await service.ProcessReportAsync(Report("M4ini", ExitedLiveNativeRunner()), store, cleanup, Now);

        Assert.Equal(RunnerInstanceStatus.Stopped, (await store.Get<RunnerInstance>(LiveNativeInstanceId))!.Status);
        Assert.Equal("pending", (await store.Get<WebhookEvent>(LiveNativeEventId))!.Status);
        Assert.Contains(api.Requests, r => r.Method == HttpMethod.Delete);
    }

    // RunnerTimeoutService gives a tracked runner the provider cannot answer about twice the
    // configured running timeout. Reconciliation hard-coded 12h for a running orphan, so with
    // a 24h timeout it destroyed an orphan at 12h that a tracked runner would keep for 48h.
    [Fact]
    public async Task RunningOrphan_ProviderUnknown_CeilingFollowsTheConfiguredRunningTimeout()
    {
        var (service, dispatcher, store, _) = await CreateReconciliationAsync(
            "M4ini",
            configuration: new() { ["DynamicProvisioning:RunningTimeoutMinutes"] = "1440" });
        var cleanup = new FakeOrphanActivity(ProviderRunnerActivity.Unknown);
        var report = Report("M4ini", RunningResource(ExecutionBackend.Tart));

        await service.ProcessReportAsync(report, store, cleanup, Now);
        await service.ProcessReportAsync(report, store, cleanup, Now.AddHours(13));
        await service.ProcessReportAsync(report, store, cleanup, Now.AddHours(47));

        Assert.DoesNotContain(dispatcher.Commands, c => c.Kind == HostCommandKind.CleanupOrphan);

        await service.ProcessReportAsync(report, store, cleanup, Now.AddHours(48).AddMinutes(1));

        var command = dispatcher.SingleCommand<CleanupOrphanCommand>(HostCommandKind.CleanupOrphan);
        Assert.Equal("rr-FashionPact-macOS-VM-jit-44578a61", command.VmName);
    }

    // Reproduces Tower 2026-10-20 07:58:39: "Marking runner NoSafeRoom-Apple-Docker-jit-0a5e827c
    // as stopped — host reported it exited (status: created)". DockerBackend creates a JIT
    // container, copies its start script in, then starts it, so a report taken in between
    // lists it "created" with IsRunning false. Reconciliation read that as exited: it stopped
    // the record, requeued the job and had the host remove the container (exit 137).
    // "restarting" and "paused" containers still exist and will run again, too.
    [Fact]
    public async Task DockerContainerNotYetOrNotCurrentlyRunning_IsNotStoppedRequeuedOrCleanedUp()
    {
        foreach (var state in new[] { "created", "Created", "restarting", "paused" })
        {
            var (service, dispatcher, store, cleanup) = await CreateReconciliationAsync("Tower");
            await InsertStartingDockerRunnerAsync(store);

            await service.ProcessReportAsync(Report("Tower", DockerRunner(state)), store, cleanup, Now);

            var record = await store.Get<RunnerInstance>(DockerInstanceId);
            Assert.True(RunnerInstanceStatus.Starting == record!.Status, $"{state}: record is {record.Status}");
            Assert.Null(record.StoppedAt);
            Assert.Null(record.HostReportedGoneAt);
            Assert.Equal("provisioned", (await store.Get<WebhookEvent>(DockerEventId))!.Status);
            Assert.DoesNotContain(dispatcher.Commands, c => c.Kind == HostCommandKind.CleanupOrphan);
        }
    }

    // Guard: a container Docker reports exited, dead or being removed has ended.
    [Fact]
    public async Task DockerContainerReportedEnded_IsStoppedRequeuedAndCleanedUp()
    {
        foreach (var state in new[] { "exited", "dead", "removing" })
        {
            var (service, dispatcher, store, cleanup) = await CreateReconciliationAsync("Tower");
            await InsertStartingDockerRunnerAsync(store);

            await service.ProcessReportAsync(Report("Tower", DockerRunner(state)), store, cleanup, Now);

            var record = await store.Get<RunnerInstance>(DockerInstanceId);
            Assert.True(RunnerInstanceStatus.Stopped == record!.Status, $"{state}: record is {record.Status}");
            Assert.Equal(Now, record.HostReportedGoneAt);
            Assert.Equal("pending", (await store.Get<WebhookEvent>(DockerEventId))!.Status);
            Assert.Equal(DockerContainerId, dispatcher.SingleCommand<CleanupOrphanCommand>(HostCommandKind.CleanupOrphan).ContainerId);
        }
    }

    // A "created" container with no record was cleaned up on the first report that listed
    // it, as "not active", skipping the grace period a running orphan gets.
    [Fact]
    public async Task DockerOrphanReportedCreated_GetsTheRunningOrphanGracePeriod()
    {
        var (service, dispatcher, store, _) = await CreateReconciliationAsync("Tower");
        var cleanup = new FakeOrphanActivity(ProviderRunnerActivity.NotRegistered);
        var report = Report("Tower", DockerRunner("created"));

        await service.ProcessReportAsync(report, store, cleanup, Now);
        await service.ProcessReportAsync(report, store, cleanup, Now.AddMinutes(9));

        Assert.DoesNotContain(dispatcher.Commands, c => c.Kind == HostCommandKind.CleanupOrphan);

        await service.ProcessReportAsync(report, store, cleanup, Now + ReconciliationService.RunningOrphanGracePeriod);

        Assert.Equal(DockerContainerId, dispatcher.SingleCommand<CleanupOrphanCommand>(HostCommandKind.CleanupOrphan).ContainerId);
    }

    // Guard: an exited or dead container with no record is still cleaned up at once.
    [Fact]
    public async Task DockerOrphanReportedEnded_IsCleanedUpImmediately()
    {
        foreach (var state in new[] { "exited", "dead" })
        {
            var (service, dispatcher, store, _) = await CreateReconciliationAsync("Tower");
            var cleanup = new FakeOrphanActivity(ProviderRunnerActivity.Busy);

            await service.ProcessReportAsync(Report("Tower", DockerRunner(state)), store, cleanup, Now);

            Assert.Equal(DockerContainerId, dispatcher.SingleCommand<CleanupOrphanCommand>(HostCommandKind.CleanupOrphan).ContainerId);
        }
    }

    [Fact]
    public void IsRunnerStillActive_OnlyDockerTransitionalStatesCountAsActive()
    {
        foreach (var state in new[] { "created", "CREATED", "restarting", "paused" })
            Assert.True(ReconciliationService.IsRunnerStillActive(DockerRunner(state)), state);
        foreach (var state in new[] { "exited", "dead", "removing" })
            Assert.False(ReconciliationService.IsRunnerStillActive(DockerRunner(state)), state);

        // Other backends keep their own vocabulary.
        Assert.False(ReconciliationService.IsRunnerStillActive(new DiscoveredRunnerInfo
        {
            RunnerName = "FashionPact-macOS-VM-jit-44578a61",
            VmName = "rr-FashionPact-macOS-VM-jit-44578a61",
            Backend = ExecutionBackend.Tart,
            IsRunning = false,
            Status = "paused"
        }));
    }

    // A container that failed to start stays "created". It has no process, so it cannot be on
    // a job, but "created" counted as active forever: the static record matching it was never
    // ended, and once the Starting timeout failed it, its health kept being refreshed, so
    // neither the record nor the container was ever removed. "created" means starting only
    // within the deploy window.
    [Fact]
    public async Task StaticDockerRunnerStillCreatedLongAfterItsDeploy_IsStoppedAndCleanedUp()
    {
        var (service, dispatcher, store, cleanup) = await CreateReconciliationAsync("Tower");
        await InsertStaticDockerRunnerAsync(store, RunnerInstanceStatus.Starting, deployedAt: Now.AddMinutes(-10));
        var report = Report("Tower", DockerRunner("created"));

        // Listed "created" since just after its deploy.
        await service.ProcessReportAsync(report, store, cleanup, Now.AddMinutes(-10).AddSeconds(30));
        await service.ProcessReportAsync(report, store, cleanup, Now);

        var record = await store.Get<RunnerInstance>(DockerInstanceId);
        Assert.Equal(RunnerInstanceStatus.Stopped, record!.Status);
        Assert.Equal(Now, record.HostReportedGoneAt);
        Assert.Equal(DockerContainerId, dispatcher.SingleCommand<CleanupOrphanCommand>(HostCommandKind.CleanupOrphan).ContainerId);
    }

    // The record the Starting timeout failed: its "created" container is reported gone rather
    // than refreshing the record's health, so the record can be removed and the container
    // cleaned up as an orphan.
    [Fact]
    public async Task FailedStaticDockerRecordWhoseContainerIsStillCreated_IsReportedGone()
    {
        var (service, _, store, cleanup) = await CreateReconciliationAsync("Tower");
        var firstReported = Now.AddMinutes(-11).AddSeconds(30);
        await InsertStaticDockerRunnerAsync(store, RunnerInstanceStatus.Failed, deployedAt: Now.AddMinutes(-11));
        var report = Report("Tower", DockerRunner("created"));

        // Listed "created" since just after its deploy.
        await service.ProcessReportAsync(report, store, cleanup, firstReported);
        await service.ProcessReportAsync(report, store, cleanup, Now);

        var record = await store.Get<RunnerInstance>(DockerInstanceId);
        Assert.Equal(Now, record!.HostReportedGoneAt);
        Assert.Equal(firstReported, record.LastHealthCheck);
    }

    // DeployedAt is stamped when the deploy command is dispatched, before the host queues it,
    // pulls the image and creates the container. A deploy that spent over 5 minutes on those
    // had its container judged "created" past the window on the first report that listed
    // it, seconds after Docker created it: the record was stopped, the container removed and
    // the runner deregistered as it started (the 07:58:39 kill again). The window runs from
    // the first report that lists the container "created" when that is later.
    [Theory]
    [InlineData(RunnerInstanceStatus.Starting)]
    [InlineData(RunnerInstanceStatus.Running)]
    public async Task DockerContainerFirstReportedCreatedLongAfterItsDeploy_IsEndedOnlyAWindowAfterThatReport(RunnerInstanceStatus status)
    {
        var (service, dispatcher, store, cleanup, api) = await ExitedScenarioAsync(api => api
            .RespondJson(IsRepoLookup, RunnersJson((LiveNativeRunner, false)))
            .Respond(IsRunnerDelete, _ => new HttpResponseMessage(HttpStatusCode.NoContent)));
        var record = (await store.Get<RunnerInstance>(LiveNativeInstanceId))!;
        record.ProcessId = null;
        record.ContainerId = DockerContainerId;
        record.Status = status;
        record.DeployedAt = Now.AddMinutes(-10);
        record.StartedAt = status == RunnerInstanceStatus.Running ? Now.AddMinutes(-1) : null;
        await store.Update(record);
        await store.Insert(new Host { Id = "Tower", Name = "Tower", Platform = HostPlatform.Linux, AgentStatus = AgentStatus.Online });
        var report = Report("M4ini", new DiscoveredRunnerInfo
        {
            InstanceId = LiveNativeInstanceId,
            RunnerName = LiveNativeRunner,
            ContainerId = DockerContainerId,
            Backend = ExecutionBackend.Docker,
            IsRunning = false,
            Status = "created"
        });

        await service.ProcessReportAsync(report, store, cleanup, Now);
        // Another host's report says nothing about M4ini's containers.
        await service.ProcessReportAsync(Report("Tower"), store, cleanup, Now.AddMinutes(1));
        await service.ProcessReportAsync(report, store, cleanup, Now.AddMinutes(4));

        record = (await store.Get<RunnerInstance>(LiveNativeInstanceId))!;
        Assert.Equal(status, record.Status);
        Assert.Null(record.StoppedAt);
        Assert.Null(record.HostReportedGoneAt);
        Assert.Equal(Now.AddMinutes(4), record.LastHealthCheck);
        Assert.Equal("provisioned", (await store.Get<WebhookEvent>(LiveNativeEventId))!.Status);
        Assert.DoesNotContain(dispatcher.Commands, c => c.Kind == HostCommandKind.CleanupOrphan);
        Assert.DoesNotContain(api.Requests, IsDeregistration);

        // Still "created" a whole window after the first report that listed it: it never started.
        await service.ProcessReportAsync(report, store, cleanup, Now.AddMinutes(5));

        record = (await store.Get<RunnerInstance>(LiveNativeInstanceId))!;
        Assert.Equal(RunnerInstanceStatus.Stopped, record.Status);
        Assert.Equal(Now.AddMinutes(5), record.HostReportedGoneAt);
        Assert.Equal(DockerContainerId, dispatcher.SingleCommand<CleanupOrphanCommand>(HostCommandKind.CleanupOrphan).ContainerId);
        Assert.Contains(api.Requests, r => r.Method == HttpMethod.Delete);
    }

    // Guard: "restarting" and "paused" containers have a process, so they are not ended
    // however long ago they were deployed.
    [Fact]
    public async Task DockerRunnerRestartingOrPausedLongAfterItsDeploy_IsNotEnded()
    {
        foreach (var state in new[] { "restarting", "paused" })
        {
            var (service, dispatcher, store, cleanup) = await CreateReconciliationAsync("Tower");
            await InsertStaticDockerRunnerAsync(store, RunnerInstanceStatus.Starting, deployedAt: Now.AddMinutes(-10));

            await service.ProcessReportAsync(Report("Tower", DockerRunner(state)), store, cleanup, Now);

            var record = await store.Get<RunnerInstance>(DockerInstanceId);
            Assert.True(RunnerInstanceStatus.Starting == record!.Status, $"{state}: record is {record.Status}");
            Assert.Null(record.HostReportedGoneAt);
            Assert.Equal(Now, record.LastHealthCheck);
            Assert.DoesNotContain(dispatcher.Commands, c => c.Kind == HostCommandKind.CleanupOrphan);
        }
    }

    // An orphan still "created" after the running-orphan grace period was never started, so
    // it has no job to protect: it is cleaned up without asking the provider, whose Busy or
    // Unknown answer about a runner of the same name kept it forever.
    [Fact]
    public async Task DockerOrphanStillCreatedAfterTheGracePeriod_IsCleanedUpWithoutAskingTheProvider()
    {
        foreach (var activity in new[] { ProviderRunnerActivity.Busy, ProviderRunnerActivity.Unknown })
        {
            var (service, dispatcher, store, _) = await CreateReconciliationAsync("Tower");
            var cleanup = new FakeOrphanActivity(activity);
            var report = Report("Tower", DockerRunner("created"));

            await service.ProcessReportAsync(report, store, cleanup, Now);
            await service.ProcessReportAsync(report, store, cleanup, Now + ReconciliationService.RunningOrphanGracePeriod);

            Assert.Equal(DockerContainerId, dispatcher.SingleCommand<CleanupOrphanCommand>(HostCommandKind.CleanupOrphan).ContainerId);
            Assert.Equal(0, cleanup.Calls);
        }
    }

    // A cached Unknown may only keep an orphan. Probed a minute before the ceiling, Unknown
    // was cached for two minutes and then reused past the ceiling to clean up the resource,
    // although GitHub had meanwhile given the runner a job.
    [Fact]
    public async Task RunningOrphan_UnknownBeforeTheCeilingThenBusy_IsNotCleanedUpOnTheCachedUnknown()
    {
        var (service, dispatcher, store, _) = await CreateReconciliationAsync("M4ini");
        var cleanup = new FakeOrphanActivity(ProviderRunnerActivity.Unknown);
        var report = Report("M4ini", RunningResource(ExecutionBackend.Tart));
        var ceiling = TimeSpan.FromHours(12);

        await service.ProcessReportAsync(report, store, cleanup, Now);
        await service.ProcessReportAsync(report, store, cleanup, Now + ceiling - TimeSpan.FromMinutes(1));
        Assert.Equal(1, cleanup.Calls);

        cleanup.Activity = ProviderRunnerActivity.Busy;
        await service.ProcessReportAsync(report, store, cleanup, Now + ceiling + TimeSpan.FromSeconds(30));

        Assert.DoesNotContain(dispatcher.Commands, c => c.Kind == HostCommandKind.CleanupOrphan);
        Assert.Equal(2, cleanup.Calls);

        // The Busy answer only keeps the resource, so it is reused.
        await service.ProcessReportAsync(report, store, cleanup, Now + ceiling + TimeSpan.FromSeconds(60));

        Assert.DoesNotContain(dispatcher.Commands, c => c.Kind == HostCommandKind.CleanupOrphan);
        Assert.Equal(2, cleanup.Calls);
    }

    // Guard: past the ceiling a fresh Unknown still lets the orphan go.
    [Fact]
    public async Task RunningOrphan_UnknownBeforeTheCeilingAndAgainAfterIt_IsCleanedUpOnTheFreshAnswer()
    {
        var (service, dispatcher, store, _) = await CreateReconciliationAsync("M4ini");
        var cleanup = new FakeOrphanActivity(ProviderRunnerActivity.Unknown);
        var report = Report("M4ini", RunningResource(ExecutionBackend.Tart));
        var ceiling = TimeSpan.FromHours(12);

        await service.ProcessReportAsync(report, store, cleanup, Now);
        await service.ProcessReportAsync(report, store, cleanup, Now + ceiling - TimeSpan.FromMinutes(1));
        await service.ProcessReportAsync(report, store, cleanup, Now + ceiling + TimeSpan.FromSeconds(30));

        Assert.Equal(2, cleanup.Calls);
        Assert.Equal("rr-FashionPact-macOS-VM-jit-44578a61", dispatcher.SingleCommand<CleanupOrphanCommand>(HostCommandKind.CleanupOrphan).VmName);
    }

    // HostWorkerEventProcessor.RunnerStartedAsync sets a record Running whatever its status,
    // so a record reconciliation ended on Unknown can be live again a report later. The
    // Unknown it ended on was cached, and the next report that missed the runner ended it
    // again on that answer without asking GitHub, which by then had given it a job.
    [Fact]
    public async Task RunnerMissingFromAReport_IsNotEndedOnACachedUnknown()
    {
        var githubDown = true;
        var (service, store, cleanup, api) = await ProviderScenarioAsync(api => api
            .Respond(IsRepoLookup, _ => githubDown
                ? FakeProviderHttpApi.JsonResponse("""{"message":"boom"}""", HttpStatusCode.InternalServerError)
                : FakeProviderHttpApi.JsonResponse(RunnersJson((LiveNativeRunner, true)))));

        await service.ProcessReportAsync(Report("M4ini"), store, cleanup, Now);

        var record = await store.Get<RunnerInstance>(LiveNativeInstanceId);
        Assert.Equal(RunnerInstanceStatus.Crashed, record!.Status);

        record.Status = RunnerInstanceStatus.Running;
        record.StartedAt = Now.AddSeconds(10);
        record.StoppedAt = null;
        record.HostReportedGoneAt = null;
        await store.Update(record);
        githubDown = false;
        var probes = api.Requests.Count(IsActivityProbe);

        await service.ProcessReportAsync(Report("M4ini"), store, cleanup, Now.AddSeconds(30));

        record = await store.Get<RunnerInstance>(LiveNativeInstanceId);
        Assert.Equal(RunnerInstanceStatus.Running, record!.Status);
        Assert.Null(record.HostReportedGoneAt);
        Assert.True(api.Requests.Count(IsActivityProbe) > probes);
    }

    private const string DockerInstanceId = "0a5e827c-6c1e-4f43-9d2b-1f0e8f3a9b11";
    private const string DockerRunnerName = "NoSafeRoom-Apple-Docker-jit-0a5e827c";
    private const string DockerContainerId = "3f1c9d8e2b7a4c6d5e0f1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b1c2d";
    private const string DockerEventId = "evt-118640012345";

    private static DiscoveredRunnerInfo DockerRunner(string state) => new()
    {
        InstanceId = DockerInstanceId,
        RunnerName = DockerRunnerName,
        ContainerId = DockerContainerId,
        Backend = ExecutionBackend.Docker,
        IsRunning = state == "running",
        Status = state
    };

    // A dynamic Docker runner whose deploy command has just created its container.
    private static async Task InsertStartingDockerRunnerAsync(IDocumentStore store)
    {
        await store.Insert(new WebhookEvent
        {
            Id = DockerEventId,
            Action = "queued",
            Status = "provisioned",
            Repository = "Clancey/NoSafeRoom",
            JobId = "118640012345",
            Provider = RunnerProvider.GitHubActions.ToString()
        });
        await store.Insert(new RunnerInstance
        {
            Id = DockerInstanceId,
            HostId = "Tower",
            RunnerName = DockerRunnerName,
            ContainerId = DockerContainerId,
            WebhookEventId = DockerEventId,
            JobId = "118640012345",
            ProvisioningMode = "dynamic",
            Status = RunnerInstanceStatus.Starting,
            DeployedAt = Now.AddSeconds(-20)
        });
    }

    private static Task InsertStaticDockerRunnerAsync(
        IDocumentStore store,
        RunnerInstanceStatus status,
        DateTime deployedAt,
        DateTime? stoppedAt = null,
        DateTime? lastHealthCheck = null) =>
        store.Insert(new RunnerInstance
        {
            Id = DockerInstanceId,
            HostId = "Tower",
            RunnerName = DockerRunnerName,
            ContainerId = DockerContainerId,
            ProvisioningMode = "static",
            Status = status,
            CreatedAt = deployedAt.AddSeconds(-5),
            DeployedAt = deployedAt,
            StoppedAt = stoppedAt,
            LastHealthCheck = lastHealthCheck
        });

    private const string LiveNativeInstanceId = "516c3058";
    private const string LiveNativeRunner = "Macos-jit-516c3058";
    private const string LiveNativeEventId = "evt-118606912280";

    private static DiscoveredRunnerInfo ExitedLiveNativeRunner() => new()
    {
        InstanceId = LiveNativeInstanceId,
        RunnerName = LiveNativeRunner,
        ProcessId = 3331,
        Backend = ExecutionBackend.Native,
        IsRunning = false,
        Status = "exited"
    };

    private static async Task<(ReconciliationService Service, IDocumentStore Store, RunnerRegistrationCleanupService Cleanup, FakeProviderHttpApi Api)>
        ProviderScenarioAsync(Action<FakeProviderHttpApi> routes, ILogger<ReconciliationService>? logger = null)
    {
        var (service, _, store, cleanup, api) = await ExitedScenarioAsync(routes, logger);
        return (service, store, cleanup, api);
    }

    // A running dynamic native runner on M4ini, deployed for a queued FashionPactPos job whose
    // in_progress webhook never arrived, registered on GitHub under a personal-account repo.
    private static async Task<(ReconciliationService Service, RecordingHostCommandDispatcher Dispatcher, IDocumentStore Store, RunnerRegistrationCleanupService Cleanup, FakeProviderHttpApi Api)>
        ExitedScenarioAsync(Action<FakeProviderHttpApi> routes, ILogger<ReconciliationService>? logger = null)
    {
        var (service, dispatcher, store, _) = await CreateReconciliationAsync("M4ini", logger);
        await store.Insert(new ProviderCredential
        {
            Id = "cred-gh",
            Name = "github",
            Provider = RunnerProvider.GitHubActions,
            GitHubOrg = "Clancey",
            GitHubToken = "ghp_test"
        });
        await store.Insert(new RunnerProfile
        {
            Id = "macos-native",
            Name = "Macos",
            Provider = RunnerProvider.GitHubActions,
            ExecutionBackend = ExecutionBackend.Native,
            ProviderCredentialId = "cred-gh"
        });
        await store.Insert(new WebhookEvent
        {
            Id = LiveNativeEventId,
            Action = "queued",
            Status = "provisioned",
            Repository = "Clancey/FashionPactPos",
            JobId = "118606912280",
            Provider = RunnerProvider.GitHubActions.ToString()
        });
        await store.Insert(new RunnerInstance
        {
            Id = LiveNativeInstanceId,
            HostId = "M4ini",
            ProfileId = "macos-native",
            RunnerName = LiveNativeRunner,
            ProcessId = 3331,
            WebhookEventId = LiveNativeEventId,
            JobId = "118606912280",
            ProvisioningMode = "dynamic",
            Status = RunnerInstanceStatus.Running,
            DeployedAt = Now.AddMinutes(-9),
            StartedAt = Now.AddMinutes(-7),
            LastHealthCheck = Now.AddMinutes(-1)
        });

        var api = new FakeProviderHttpApi();
        routes(api);
        var provider = new GitHubActionsProvider(
            api,
            new GitHubAuthenticationService(api, NullLogger<GitHubAuthenticationService>.Instance),
            NullLogger<GitHubActionsProvider>.Instance);
        var cleanup = new RunnerRegistrationCleanupService([provider], NullLogger<RunnerRegistrationCleanupService>.Instance);
        return (service, dispatcher, store, cleanup, api);
    }

    private static bool IsRepoLookup(HttpRequestMessage request) =>
        request.Method == HttpMethod.Get
        && request.RequestUri!.AbsolutePath == "/repos/Clancey/FashionPactPos/actions/runners";

    private static bool IsRunnerDelete(HttpRequestMessage request) =>
        request.Method == HttpMethod.Delete
        && request.RequestUri!.AbsolutePath.StartsWith("/repos/Clancey/FashionPactPos/actions/runners/", StringComparison.Ordinal);

    private static bool IsActivityProbe(RecordedHttpRequest request) =>
        request.Method == HttpMethod.Get
        && request.PathAndQuery.StartsWith($"/repos/Clancey/FashionPactPos/actions/runners?name={LiveNativeRunner}&", StringComparison.Ordinal);

    // RemoveRunnerAsync lists the scope's runners, then deletes the match.
    private static bool IsDeregistration(RecordedHttpRequest request) =>
        request.Method == HttpMethod.Delete
        || request.PathAndQuery == "/repos/Clancey/FashionPactPos/actions/runners?per_page=100";

    private static string RunnersJson(params (string Name, bool Busy)[] runners)
    {
        var items = runners.Select((r, i) =>
            $$"""{"id":{{i + 1}},"name":"{{r.Name}}","os":"macOS","status":"online","busy":{{(r.Busy ? "true" : "false")}}}""");
        return $$"""{"total_count":{{runners.Length}},"runners":[{{string.Join(",", items)}}]}""";
    }

    [Theory]
    [InlineData(ExecutionBackend.Tart, "rr-FashionPact-macOS-VM-jit-44578a61")]
    [InlineData(ExecutionBackend.Native, "3651")]
    [InlineData(ExecutionBackend.Docker, "f98754d595f4032f0ee1d4dbcd8925cc027681ccaa4fad38a5a663532416c6fb")]
    public void DescribeResource_NamesTheBackendHandle(ExecutionBackend backend, string expected)
    {
        Assert.Equal(expected, ReconciliationService.DescribeResource(RunningResource(backend)));
    }

    private static DiscoveredRunnerInfo RunningResource(ExecutionBackend backend) => backend switch
    {
        ExecutionBackend.Tart => new DiscoveredRunnerInfo
        {
            RunnerName = "FashionPact-macOS-VM-jit-44578a61",
            VmName = "rr-FashionPact-macOS-VM-jit-44578a61",
            Backend = ExecutionBackend.Tart,
            IsRunning = true,
            Status = "running"
        },
        ExecutionBackend.Native => new DiscoveredRunnerInfo
        {
            RunnerName = "Macos-jit-073525a2",
            ProcessId = 3651,
            Backend = ExecutionBackend.Native,
            IsRunning = true,
            Status = "running"
        },
        _ => new DiscoveredRunnerInfo
        {
            RunnerName = "ll-linux-jit-e38466ec",
            ContainerId = "f98754d595f4032f0ee1d4dbcd8925cc027681ccaa4fad38a5a663532416c6fb",
            Backend = ExecutionBackend.Docker,
            IsRunning = true,
            Status = "running"
        }
    };

    private static ReconciliationReport Report(string hostId, params DiscoveredRunnerInfo[] runners)
        => new() { HostId = hostId, Runners = [.. runners], ReportsBackendAvailability = true };

    private sealed class FakeOrphanActivity(ProviderRunnerActivity activity)
        : RunnerRegistrationCleanupService([], NullLogger<RunnerRegistrationCleanupService>.Instance)
    {
        public int Calls { get; private set; }

        public ProviderRunnerActivity Activity { get; set; } = activity;

        public override Task<ProviderRunnerActivity?> GetOrphanRunnerActivityAsync(
            IDocumentStore store,
            DiscoveredRunnerInfo runner,
            CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<ProviderRunnerActivity?>(Activity);
        }
    }

    private static async Task<(ReconciliationService Service, RecordingHostCommandDispatcher Dispatcher, IDocumentStore Store, RunnerRegistrationCleanupService Cleanup)>
        CreateReconciliationAsync(
            string hostId,
            ILogger<ReconciliationService>? logger = null,
            bool withWebhookEventsTable = true,
            Dictionary<string, string?>? configuration = null)
    {
        var store = withWebhookEventsTable
            ? await TestDocumentStore.CreateWithWebhookEventsTableAsync()
            : TestDocumentStore.Create();
        await store.Insert(new Host { Id = hostId, Name = hostId, Platform = HostPlatform.MacOS, AgentStatus = AgentStatus.Online });

        // The blanked record left behind at 11:51:21: its host and runner name were overwritten.
        await store.Insert(new RunnerInstance
        {
            Id = "02b717c2",
            HostId = "",
            RunnerName = "",
            ProvisioningMode = "dynamic",
            Status = RunnerInstanceStatus.Crashed
        });

        var dispatcher = new RecordingHostCommandDispatcher();
        // Built the way the host builds hosted services, so it gets the configuration.
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(configuration ?? []).Build())
            .AddSingleton(logger ?? NullLogger<ReconciliationService>.Instance)
            .AddSingleton<IHostCommandDispatcher>(dispatcher)
            .BuildServiceProvider();
        var service = ActivatorUtilities.CreateInstance<ReconciliationService>(services);
        var cleanup = new RunnerRegistrationCleanupService([], NullLogger<RunnerRegistrationCleanupService>.Instance);
        return (service, dispatcher, store, cleanup);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_entries)
                    return _entries.Select(e => e.Message).ToArray();
            }
        }

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_entries)
                    return _entries.ToArray();
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
                _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
