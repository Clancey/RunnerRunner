using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RunnerRunner.Agent.Backends;
using RunnerRunner.Agent.Services;
using RunnerRunner.Core.HostWorkers;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;
using RunnerRunner.HostWorker.Services;
using RunnerRunner.HostWorker.Tests.TestSupport;

namespace RunnerRunner.HostWorker.Tests.Services;

public class HostCommandProcessorTests
{
    [Fact]
    public async Task DeployRunner_StartSuccess_PublishesHealthStartedAndCompleted()
    {
        using var directory = HostWorkerTestDirectory.Create("command-deploy-success");
        var native = new FakeRunnerBackend
        {
            BackendType = ExecutionBackend.Native,
            StartHandler = _ => new RunnerInstanceInfo
            {
                InstanceHandle = "pid-123",
                RunnerName = "runner-1"
            }
        };
        using var processor = CreateProcessor(directory, out var sink, out _, nativeBackend: native);

        await processor.ProcessCommandAsync(CreateCommand("cmd-start", new HostCommandEnvelope
        {
            Kind = HostCommandKind.DeployRunner,
            DeployRunner = new DeployRunnerCommand
            {
                InstanceId = "inst-1",
                ProfileId = "profile-1",
                RunnerName = "runner-1",
                Backend = ExecutionBackend.Native,
                Provider = RunnerProvider.GitHubActions,
                Labels = ["linux", "self-hosted"]
            }
        }), CancellationToken.None);

        Assert.Equal(
            [
                HostWorkerMessageKinds.CommandAccepted,
                HostWorkerMessageKinds.RunnerHealth,
                HostWorkerMessageKinds.RunnerStarted,
                HostWorkerMessageKinds.CommandCompleted
            ],
            NonLogKinds(sink));
        Assert.Single(native.StartRequests);

        var started = SinglePayload<RunnerStartedEvent>(sink, HostWorkerMessageKinds.RunnerStarted);
        Assert.Equal("inst-1", started.InstanceId);
        Assert.Equal("pid-123", started.InstanceHandle);
        Assert.Equal(ExecutionBackend.Native, started.Backend);

        var journal = await File.ReadAllTextAsync(
            Path.Combine(directory.Path, "journals", "commands.jsonl"));
        Assert.Contains("\"status\":\"received\"", journal);
        Assert.Contains("\"status\":\"completed\"", journal);
    }

    [Fact]
    public async Task DeployRunner_BackendUnavailable_PublishesFailedStopWithoutStarting()
    {
        using var directory = HostWorkerTestDirectory.Create("command-backend-unavailable");
        var docker = new FakeRunnerBackend
        {
            BackendType = ExecutionBackend.Docker,
            IsAvailable = false
        };
        using var processor = CreateProcessor(directory, out var sink, out _, dockerBackend: docker);

        await processor.ProcessCommandAsync(CreateCommand("cmd-unavailable", new HostCommandEnvelope
        {
            Kind = HostCommandKind.DeployRunner,
            DeployRunner = new DeployRunnerCommand
            {
                InstanceId = "inst-unavailable",
                ProfileId = "profile-1",
                RunnerName = "runner-unavailable",
                Backend = ExecutionBackend.Docker,
                Provider = RunnerProvider.GitHubActions
            }
        }), CancellationToken.None);

        Assert.Empty(docker.StartRequests);
        Assert.Contains(HostWorkerMessageKinds.CommandCompleted, NonLogKinds(sink));
        Assert.DoesNotContain(HostWorkerMessageKinds.CommandRejected, NonLogKinds(sink));

        var stopped = SinglePayload<RunnerStoppedEvent>(sink, HostWorkerMessageKinds.RunnerStopped);
        Assert.Equal("failed", stopped.Reason);
        Assert.Contains("not available", stopped.ErrorMessage);
    }

    [Fact]
    public async Task DeployRunner_StartFailure_PublishesFailedStopAndRejectsCommand()
    {
        using var directory = HostWorkerTestDirectory.Create("command-deploy-failure");
        var native = new FakeRunnerBackend
        {
            BackendType = ExecutionBackend.Native,
            StartHandler = _ => throw new InvalidOperationException("simulated start failure")
        };
        using var processor = CreateProcessor(directory, out var sink, out var lifecycle, nativeBackend: native);

        await processor.ProcessCommandAsync(CreateCommand("cmd-start-fails", new HostCommandEnvelope
        {
            Kind = HostCommandKind.DeployRunner,
            DeployRunner = new DeployRunnerCommand
            {
                InstanceId = "inst-fails",
                ProfileId = "profile-1",
                RunnerName = "runner-fails",
                Backend = ExecutionBackend.Native,
                Provider = RunnerProvider.GitHubActions
            }
        }), CancellationToken.None);

        Assert.Empty(lifecycle.RunningInstances);
        Assert.DoesNotContain(HostWorkerMessageKinds.CommandCompleted, NonLogKinds(sink));

        var stopped = SinglePayload<RunnerStoppedEvent>(sink, HostWorkerMessageKinds.RunnerStopped);
        Assert.Equal("failed", stopped.Reason);
        Assert.Contains("simulated start failure", stopped.ErrorMessage);

        var rejected = SinglePayload<HostWorkerCommandStatus>(sink, HostWorkerMessageKinds.CommandRejected);
        Assert.False(rejected.Success);
        Assert.Contains("simulated start failure", rejected.Error);
    }

    [Fact]
    public async Task StopRunner_StopFailure_PublishesFailedStopAndRejectsCommand()
    {
        using var directory = HostWorkerTestDirectory.Create("command-stop-failure");
        var native = new FakeRunnerBackend
        {
            BackendType = ExecutionBackend.Native,
            StartHandler = _ => new RunnerInstanceInfo
            {
                InstanceHandle = "pid-456",
                RunnerName = "runner-stop"
            },
            StopAsyncHandler = (_, _) => throw new InvalidOperationException("simulated stop failure")
        };
        using var processor = CreateProcessor(directory, out var sink, out var lifecycle, nativeBackend: native);
        await lifecycle.StartRunnerAsync(new DeployRunnerCommand
        {
            InstanceId = "inst-stop",
            ProfileId = "profile-1",
            RunnerName = "runner-stop",
            Backend = ExecutionBackend.Native,
            Provider = RunnerProvider.GitHubActions
        }, native);

        await processor.ProcessCommandAsync(CreateCommand("cmd-stop-fails", new HostCommandEnvelope
        {
            Kind = HostCommandKind.StopRunner,
            StopRunner = new StopRunnerCommand
            {
                InstanceId = "inst-stop"
            }
        }), CancellationToken.None);

        Assert.Equal(["pid-456"], native.StopRequests);
        Assert.Empty(lifecycle.RunningInstances);

        var stopped = SinglePayload<RunnerStoppedEvent>(sink, HostWorkerMessageKinds.RunnerStopped);
        Assert.Equal("failed", stopped.Reason);
        Assert.Contains("Stop failed: simulated stop failure", stopped.ErrorMessage);

        var rejected = SinglePayload<HostWorkerCommandStatus>(sink, HostWorkerMessageKinds.CommandRejected);
        Assert.False(rejected.Success);
        Assert.Contains("simulated stop failure", rejected.Error);
    }

    // Reproduces M4ini 2026-10-20 11:46:48: the server stopped native instances 516c3058,
    // c5235957 and 2cdc03c7 by handle after the HostWorker had stopped tracking them. Docker
    // rejected the unknown id, then TartBackend ran `tart stop 3331` -- which returns 2
    // ("the specified VM "3331" does not exist") without throwing -- so the native backend that
    // owns PID 3331 was never asked.
    [Fact]
    public Task StopRunner_UntrackedNativeHandle_GoesToTheNativeBackendOnly() =>
        AssertUntrackedNativeHandleGoesToNativeOnly(ExecutionBackend.Native);

    // Same as above for a server that predates StopRunnerCommand.Backend: a numeric handle is a PID.
    [Fact]
    public Task StopRunner_UntrackedNumericHandleWithoutBackend_GoesToTheNativeBackendOnly() =>
        AssertUntrackedNativeHandleGoesToNativeOnly(backend: null);

    private async Task AssertUntrackedNativeHandleGoesToNativeOnly(ExecutionBackend? backend)
    {
        using var directory = HostWorkerTestDirectory.Create("command-stop-native-handle");
        var docker = new FakeRunnerBackend
        {
            BackendType = ExecutionBackend.Docker,
            StopAsyncHandler = (handle, _) => throw new InvalidOperationException($"No such container: {handle}")
        };
        var tart = new FakeRunnerBackend { BackendType = ExecutionBackend.Tart };
        var native = new FakeRunnerBackend { BackendType = ExecutionBackend.Native };
        using var processor = CreateProcessor(directory, out _, out _, docker, tart, native);

        await processor.ProcessCommandAsync(CreateCommand("cmd-stop-pid", new HostCommandEnvelope
        {
            Kind = HostCommandKind.StopRunner,
            StopRunner = new StopRunnerCommand
            {
                InstanceId = "516c3058",
                InstanceHandle = "3331",
                Backend = backend
            }
        }), CancellationToken.None);

        Assert.Equal(["3331"], native.StopRequests);
        Assert.Empty(tart.StopRequests);
        Assert.Empty(docker.StopRequests);
    }

    [Fact]
    public async Task StopRunner_UntrackedTartHandle_GoesToTheTartBackendOnly()
    {
        using var directory = HostWorkerTestDirectory.Create("command-stop-tart-handle");
        var docker = new FakeRunnerBackend { BackendType = ExecutionBackend.Docker };
        var tart = new FakeRunnerBackend { BackendType = ExecutionBackend.Tart };
        var native = new FakeRunnerBackend { BackendType = ExecutionBackend.Native };
        using var processor = CreateProcessor(directory, out _, out _, docker, tart, native);

        await processor.ProcessCommandAsync(CreateCommand("cmd-stop-vm", new HostCommandEnvelope
        {
            Kind = HostCommandKind.StopRunner,
            StopRunner = StopRunnerCommand.ForInstance("02b717c2", containerId: null, vmName: "rr-FashionPact-macOS-VM-jit-44578a61", processId: null)
        }), CancellationToken.None);

        Assert.Equal(["rr-FashionPact-macOS-VM-jit-44578a61"], tart.StopRequests);
        Assert.Empty(docker.StopRequests);
        Assert.Empty(native.StopRequests);
    }

    // When `tart list` failed while the HostWorker built its reconciliation report, TartBackend
    // swallowed the error and the report listed no VMs. The server reads a missing runner as
    // one that exited: it marked each running VM's instance crashed and deregistered it, so a
    // VM mid-job was ended over a failed listing. The report must say Tart could not be listed.
    [Fact]
    public async Task ReconciliationReport_WhenTartCannotBeListed_FlagsTartInsteadOfReportingNoVms()
    {
        using var directory = HostWorkerTestDirectory.Create("reconcile-tart-unavailable");
        var listFails = true;
        var tart = new TartBackend(
            NullLogger<TartBackend>.Instance,
            (_, args, _) => Task.FromResult(args switch
            {
                "--version" => (0, "2.28.0"),
                "list --format json" when listFails => (1, "Error: Operation not permitted"),
                "list --format json" => (0, """[{"Name":"rr-FashionPact-macOS-VM-jit-b848a12a","State":"running"}]"""),
                _ => throw new InvalidOperationException($"unexpected tart {args}")
            }));
        using var processor = CreateProcessor(directory, out _, out _, tartBackend: tart);

        var failed = await processor.BuildReconciliationReportAsync(CancellationToken.None);

        Assert.Empty(failed.Runners);
        Assert.Equal([ExecutionBackend.Tart], failed.UnavailableBackends);
        Assert.True(failed.ReportsBackendAvailability);

        listFails = false;
        var listed = await processor.BuildReconciliationReportAsync(CancellationToken.None);

        var vm = Assert.Single(listed.Runners);
        Assert.Equal("rr-FashionPact-macOS-VM-jit-b848a12a", vm.VmName);
        Assert.True(vm.IsRunning);
        Assert.Empty(listed.UnavailableBackends);
    }

    // Host.RunnerBasePath (set per host in Hosts.razor) and the HostWorker's own
    // Runner:BasePath both put native runners outside the default base path, which was the
    // only one discovery scanned. Every live runner there was missing from the report, so
    // the server marked it crashed, requeued its job and deregistered it mid-job.
    [Fact]
    public async Task ReconciliationReport_ListsLiveNativeRunnersUnderTheConfiguredRunnerBasePath()
    {
        using var directory = HostWorkerTestDirectory.Create("reconcile-native-configured-path");
        var configuredBasePath = Path.Combine(directory.Path, "configured-runners");
        WriteNativeInstance(configuredBasePath, "rr-live", "inst-live", "Macos-jit-live", Environment.ProcessId);
        using var processor = CreateProcessor(
            directory, out _, out _,
            nativeBackend: CreateNativeBackend(directory),
            runnerBasePath: configuredBasePath);

        var report = await processor.BuildReconciliationReportAsync(CancellationToken.None);

        var runner = Assert.Single(report.Runners);
        Assert.Equal("inst-live", runner.InstanceId);
        Assert.Equal(Environment.ProcessId, runner.ProcessId);
        Assert.True(runner.IsRunning);
        Assert.Empty(report.UnavailableBackends);
    }

    [Fact]
    public async Task ReconciliationReport_ListsLiveNativeRunnersUnderTheHostBasePathADeployUsed_AfterARestart()
    {
        using var directory = HostWorkerTestDirectory.Create("reconcile-native-host-path");
        var hostBasePath = Path.Combine(directory.Path, "host-runners");
        // A cached agent without config scripts fails the deploy after it has picked the
        // base path but before it downloads or deploys anything.
        var agentDir = Path.Combine(hostBasePath, "agents", "githubactions", "2.999.0");
        Directory.CreateDirectory(agentDir);
        await File.WriteAllTextAsync(Path.Combine(agentDir, "placeholder"), "");
        using (var processor = CreateProcessor(directory, out var sink, out _, nativeBackend: CreateNativeBackend(directory)))
        {
            await processor.ProcessCommandAsync(CreateCommand("cmd-deploy-host-path", new HostCommandEnvelope
            {
                Kind = HostCommandKind.DeployRunner,
                DeployRunner = new DeployRunnerCommand
                {
                    InstanceId = "inst-live",
                    ProfileId = "profile-1",
                    RunnerName = "Macos-jit-live",
                    Backend = ExecutionBackend.Native,
                    Provider = RunnerProvider.GitHubActions,
                    RunnerAgentVersion = "2.999.0",
                    RunnerBasePath = hostBasePath
                }
            }), CancellationToken.None);
            Assert.Contains("missing config scripts", SinglePayload<RunnerStoppedEvent>(sink, HostWorkerMessageKinds.RunnerStopped).ErrorMessage);
        }
        WriteNativeInstance(hostBasePath, "rr-live", "inst-live", "Macos-jit-live", Environment.ProcessId);

        using var restarted = CreateProcessor(directory, out _, out _, nativeBackend: CreateNativeBackend(directory));
        var report = await restarted.BuildReconciliationReportAsync(CancellationToken.None);

        var runner = Assert.Single(report.Runners);
        Assert.Equal("inst-live", runner.InstanceId);
        Assert.True(runner.IsRunning);
        Assert.Empty(report.UnavailableBackends);
    }

    // A native instances directory that exists but cannot be listed made discovery return
    // nothing, which the server reads as every native runner having exited.
    [Fact]
    public async Task ReconciliationReport_WhenANativeInstancesDirectoryCannotBeListed_FlagsNative()
    {
        using var directory = HostWorkerTestDirectory.Create("reconcile-native-dir-error");
        var configuredBasePath = Path.Combine(directory.Path, "configured-runners");
        Directory.CreateDirectory(configuredBasePath);
        await File.WriteAllTextAsync(Path.Combine(configuredBasePath, "instances"), "not a directory");
        using var processor = CreateProcessor(
            directory, out _, out _,
            nativeBackend: CreateNativeBackend(directory),
            runnerBasePath: configuredBasePath);

        var report = await processor.BuildReconciliationReportAsync(CancellationToken.None);

        Assert.Empty(report.Runners);
        Assert.Equal([ExecutionBackend.Native], report.UnavailableBackends);
    }

    // A live runner whose instance files could not be read was skipped with a warning and
    // reported nowhere, exactly like a runner that exited.
    [Fact]
    public async Task ReconciliationReport_WhenANativeInstanceCannotBeRead_FlagsNative()
    {
        using var directory = HostWorkerTestDirectory.Create("reconcile-native-file-error");
        var defaultBasePath = Path.Combine(directory.Path, "default-runners");
        WriteNativeInstance(defaultBasePath, "rr-live", "inst-live", "Macos-jit-live", Environment.ProcessId);
        await File.WriteAllTextAsync(Path.Combine(defaultBasePath, "instances", "rr-live", "rr-instance.json"), "{ truncated");
        using var processor = CreateProcessor(directory, out _, out _, nativeBackend: CreateNativeBackend(directory));

        var report = await processor.BuildReconciliationReportAsync(CancellationToken.None);

        Assert.Empty(report.Runners);
        Assert.Equal([ExecutionBackend.Native], report.UnavailableBackends);
    }

    // Guard: a host that never ran a native runner (Tower runs Docker only) reports no
    // native runners and does not flag the backend.
    [Fact]
    public async Task ReconciliationReport_OnAHostThatNeverRanNativeRunners_DoesNotFlagNative()
    {
        using var directory = HostWorkerTestDirectory.Create("reconcile-native-none");
        using var processor = CreateProcessor(
            directory, out _, out _,
            nativeBackend: CreateNativeBackend(directory),
            runnerBasePath: Path.Combine(directory.Path, "configured-runners"));

        var report = await processor.BuildReconciliationReportAsync(CancellationToken.None);

        Assert.Empty(report.Runners);
        Assert.Empty(report.UnavailableBackends);
    }

    private static NativeBackend CreateNativeBackend(HostWorkerTestDirectory directory) =>
        new(NullLogger<NativeBackend>.Instance) { DefaultBasePath = Path.Combine(directory.Path, "default-runners") };

    private static void WriteNativeInstance(string basePath, string directoryName, string instanceId, string runnerName, int pid)
    {
        var dir = Path.Combine(basePath, "instances", directoryName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "rr-instance.json"),
            $$"""{"InstanceId":"{{instanceId}}","RunnerName":"{{runnerName}}"}""");
        File.WriteAllText(Path.Combine(dir, "rr.pid"), pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task MalformedCommandEnvelope_PublishesRejectedStatus()
    {
        using var directory = HostWorkerTestDirectory.Create("command-malformed");
        using var processor = CreateProcessor(directory, out var sink, out _);

        await processor.ProcessCommandAsync(CreateCommand("cmd-malformed", new HostCommandEnvelope
        {
            Kind = HostCommandKind.DeployRunner
        }), CancellationToken.None);

        Assert.Contains(HostWorkerMessageKinds.CommandAccepted, NonLogKinds(sink));
        Assert.DoesNotContain(HostWorkerMessageKinds.CommandCompleted, NonLogKinds(sink));

        var rejected = SinglePayload<HostWorkerCommandStatus>(sink, HostWorkerMessageKinds.CommandRejected);
        Assert.Equal("cmd-malformed", rejected.CommandId);
        Assert.False(rejected.Success);
        Assert.Contains("Unsupported or malformed", rejected.Error);
    }

    [Fact]
    public async Task GetRunnerLogs_ReturnsSnapshotWithoutRepublishingItAsLogFrame()
    {
        using var directory = HostWorkerTestDirectory.Create("command-runner-logs");
        var runnerBasePath = Path.Combine(directory.Path, "runners");
        var instanceDir = Path.Combine(runnerBasePath, "instances", "rr-native");
        Directory.CreateDirectory(instanceDir);
        await File.WriteAllTextAsync(Path.Combine(instanceDir, "rr-instance.json"), """{"InstanceId":"inst-1","RunnerName":"runner-1"}""");
        await File.WriteAllTextAsync(Path.Combine(instanceDir, "runner.log"), "native runner failed\n");
        // Fake backends don't implement native log lookup (the production type-check
        // _nativeBackend is NativeBackend won't match), so the fallback message is expected.
        using var processor = CreateProcessor(directory, out var sink, out _, runnerBasePath: runnerBasePath);

        await processor.ProcessCommandAsync(CreateCommand("cmd-runner-logs", new HostCommandEnvelope
        {
            Kind = HostCommandKind.GetRunnerLogs,
            GetRunnerLogs = new GetRunnerLogsCommand
            {
                InstanceHandle = "pid-123",
                RunnerInstanceId = "inst-1",
                TailLines = 25
            }
        }), CancellationToken.None);

        var frames = sink.Published
            .Where(message => message.Kind == HostWorkerMessageKinds.LogFrame)
            .Select(HostWorkerProtocol.DeserializePayload<HostWorkerLogFrame>)
            .ToArray();
        Assert.DoesNotContain(frames, frame => frame.StreamKind == "runner.output");

        var logs = SinglePayload<RunnerLogsEvent>(sink, HostWorkerMessageKinds.RunnerLogs);
        Assert.Equal("pid-123", logs.InstanceHandle);
        Assert.Equal("inst-1", logs.RunnerInstanceId);
        Assert.Equal("(No logs available for this runner instance)", logs.Logs);
    }

    [Fact]
    public async Task GetHostLogs_ReturnsSnapshotWithoutRepublishingItAsLogFrame()
    {
        using var directory = HostWorkerTestDirectory.Create("command-host-logs");
        using var processor = CreateProcessor(directory, out var sink, out _);

        await processor.ProcessCommandAsync(CreateCommand("cmd-host-logs", new HostCommandEnvelope
        {
            Kind = HostCommandKind.GetHostLogs,
            GetHostLogs = new GetHostLogsCommand
            {
                TailLines = 25
            }
        }), CancellationToken.None);

        var frames = sink.Published
            .Where(message => message.Kind == HostWorkerMessageKinds.LogFrame)
            .Select(HostWorkerProtocol.DeserializePayload<HostWorkerLogFrame>)
            .ToArray();
        Assert.DoesNotContain(frames, frame => frame.StreamKind == "worker.process");

        var logs = SinglePayload<HostLogsEvent>(sink, HostWorkerMessageKinds.HostLogs);
        Assert.Contains("No readable HostWorker log file was found", logs.Logs);
    }

    private static HostCommandProcessor CreateProcessor(
        HostWorkerTestDirectory directory,
        out FakeHostWorkerEventSink sink,
        out RunnerLifecycleManager lifecycle,
        IRunnerBackend? dockerBackend = null,
        IRunnerBackend? tartBackend = null,
        IRunnerBackend? nativeBackend = null,
        string? runnerBasePath = null)
    {
        var configValues = new Dictionary<string, string?>
        {
            ["HostWorker:DataRoot"] = directory.Path,
            ["HostWorker:ResourceUsageTimeoutSeconds"] = "1"
        };
        if (!string.IsNullOrWhiteSpace(runnerBasePath))
            configValues["Runner:BasePath"] = runnerBasePath;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();
        var identity = new HostWorkerIdentity("host-1", "test-host", HostPlatform.Linux, "x64");
        var paths = new HostWorkerPaths(configuration);
        var logStore = new HostWorkerLocalLogStore(paths);
        lifecycle = new RunnerLifecycleManager(NullLogger<RunnerLifecycleManager>.Instance);
        var processor = new HostCommandProcessor(
            configuration,
            identity,
            lifecycle,
            new ImageManager(NullLogger<ImageManager>.Instance),
            paths,
            logStore,
            new HostWorkerSelfUpdater(configuration, identity, paths, NullLogger<HostWorkerSelfUpdater>.Instance),
            new HostResourceUsageCollector(
                configuration,
                identity,
                paths,
                NullLogger<HostResourceUsageCollector>.Instance,
                NullLoggerFactory.Instance),
            NullLogger<HostCommandProcessor>.Instance,
            NullLoggerFactory.Instance,
            dockerBackend ?? new FakeRunnerBackend { BackendType = ExecutionBackend.Docker },
            tartBackend ?? new FakeRunnerBackend { BackendType = ExecutionBackend.Tart },
            nativeBackend ?? new FakeRunnerBackend { BackendType = ExecutionBackend.Native });

        sink = new FakeHostWorkerEventSink();
        processor.AttachEventSink(sink);
        return processor;
    }

    private static HostWorkerMessage CreateCommand(string commandId, HostCommandEnvelope envelope)
        => HostWorkerProtocol.CreateMessage(
            "host-1",
            HostWorkerMessageKinds.Command,
            envelope,
            commandId);

    private static string[] NonLogKinds(FakeHostWorkerEventSink sink)
        => sink.Published
            .Select(message => message.Kind)
            .Where(kind => kind != HostWorkerMessageKinds.LogFrame)
            .ToArray();

    private static TPayload SinglePayload<TPayload>(FakeHostWorkerEventSink sink, string kind)
        => HostWorkerProtocol.DeserializePayload<TPayload>(
            Assert.Single(sink.Published.Where(message => message.Kind == kind)));
}
