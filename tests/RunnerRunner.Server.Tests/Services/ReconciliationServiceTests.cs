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

    // The host saw the process exit, so the runner is gone and its record ends. GitHub shows
    // a dead runner as offline and not busy soon enough; while it still says busy (or cannot
    // say), the registration is left alone.
    [Fact]
    public async Task RunnerReportedExited_ProviderBusy_IsStoppedButNotDeregistered()
    {
        var (service, dispatcher, store, cleanup, api) = await ExitedScenarioAsync(
            api => api.RespondJson(IsRepoLookup, RunnersJson((LiveNativeRunner, true))));

        await service.ProcessReportAsync(Report("M4ini", ExitedLiveNativeRunner()), store, cleanup, Now);

        var record = await store.Get<RunnerInstance>(LiveNativeInstanceId);
        Assert.Equal(RunnerInstanceStatus.Stopped, record!.Status);
        Assert.Equal(Now, record.HostReportedGoneAt);
        Assert.Equal(3331, dispatcher.SingleCommand<CleanupOrphanCommand>(HostCommandKind.CleanupOrphan).ProcessId);
        Assert.DoesNotContain(api.Requests, IsDeregistration);
    }

    [Fact]
    public async Task RunnerReportedExited_ProviderUnknown_IsStoppedButNotDeregistered()
    {
        var (service, _, store, cleanup, api) = await ExitedScenarioAsync(
            api => api.RespondJson(IsRepoLookup, """{"message":"boom"}""", HttpStatusCode.InternalServerError));

        await service.ProcessReportAsync(Report("M4ini", ExitedLiveNativeRunner()), store, cleanup, Now);

        Assert.Equal(RunnerInstanceStatus.Stopped, (await store.Get<RunnerInstance>(LiveNativeInstanceId))!.Status);
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

        public override Task<ProviderRunnerActivity?> GetOrphanRunnerActivityAsync(
            IDocumentStore store,
            DiscoveredRunnerInfo runner,
            CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<ProviderRunnerActivity?>(activity);
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
