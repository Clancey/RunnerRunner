using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RunnerRunner.Agent.Backends;
using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Agent.Tests.Backends;

public sealed class NativeBackendDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Directory.GetCurrentDirectory(), "TestResults", $"native-discovery-{Guid.NewGuid():N}");

    private string DefaultBasePath => Path.Combine(_root, "default");
    private string HostBasePath => Path.Combine(_root, "host-base-path");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    // A host with a RunnerBasePath set in Hosts.razor gets its native runners deployed under
    // that path, but discovery only scanned the default one. Every live runner was missing
    // from the reconciliation report, so the server marked it crashed and deregistered it.
    // The path must still be scanned after the HostWorker restarts.
    [Fact]
    public async Task Discover_FindsALiveRunnerUnderTheBasePathAStartUsed_AfterARestart()
    {
        await StartUntilTheAgentCheckAsync(CreateBackend(), HostBasePath);
        WriteInstance(HostBasePath, "rr-live", "inst-live", "Macos-jit-live", Environment.ProcessId);

        var restarted = CreateBackend();
        var runner = Assert.Single(await restarted.DiscoverManagedProcessesAsync());

        Assert.Equal("inst-live", runner.InstanceId);
        Assert.Equal(Environment.ProcessId, runner.ProcessId);
        Assert.True(runner.IsRunning);
        Assert.Equal("running", runner.Status);
    }

    [Fact]
    public async Task Discover_ScansTheBasePathsTheCallerConfigures()
    {
        WriteInstance(HostBasePath, "rr-live", "inst-live", "Macos-jit-live", Environment.ProcessId);

        var runner = Assert.Single(await CreateBackend().DiscoverManagedProcessesAsync([HostBasePath]));

        Assert.Equal("inst-live", runner.InstanceId);
        Assert.True(runner.IsRunning);
    }

    // The same directory named three ways is scanned once, or each runner is reported twice.
    [Fact]
    public async Task Discover_ScansEachNormalizedBasePathOnce()
    {
        var backend = CreateBackend();
        await StartUntilTheAgentCheckAsync(backend, HostBasePath + Path.DirectorySeparatorChar);
        WriteInstance(HostBasePath, "rr-live", "inst-live", "Macos-jit-live", Environment.ProcessId);
        WriteInstance(DefaultBasePath, "rr-default", "inst-default", "Macos-jit-default", Environment.ProcessId);

        var runners = await backend.DiscoverManagedProcessesAsync(
            [Path.Combine(HostBasePath, "..", "host-base-path"), DefaultBasePath + Path.DirectorySeparatorChar]);

        Assert.Equal(["inst-default", "inst-live"], runners.Select(r => r.InstanceId).Order());
    }

    // An empty result would read as "every native runner exited". When an instances
    // directory exists but cannot be listed, discovery must fail instead.
    [Fact]
    public async Task Discover_WhenAnInstancesDirectoryCannotBeListed_Throws()
    {
        Directory.CreateDirectory(DefaultBasePath);
        await File.WriteAllTextAsync(Path.Combine(DefaultBasePath, "instances"), "not a directory");

        await Assert.ThrowsAnyAsync<Exception>(() => CreateBackend().DiscoverManagedProcessesAsync());
    }

    // A live runner whose instance files cannot be read used to be skipped with a warning,
    // so it was missing from the report exactly like a runner that exited.
    [Fact]
    public async Task Discover_WhenAnInstanceFileCannotBeRead_Throws()
    {
        WriteInstance(DefaultBasePath, "rr-live", "inst-live", "Macos-jit-live", Environment.ProcessId);
        WriteInstance(DefaultBasePath, "rr-broken", "inst-broken", "Macos-jit-broken", Environment.ProcessId);
        await File.WriteAllTextAsync(Path.Combine(DefaultBasePath, "instances", "rr-broken", "rr-instance.json"), "{ truncated");

        await Assert.ThrowsAnyAsync<Exception>(() => CreateBackend().DiscoverManagedProcessesAsync());
    }

    // Guard: a host that never ran a native runner (a Docker-only host) must not report
    // the native backend as unavailable.
    [Fact]
    public async Task Discover_OnAHostThatNeverRanNativeRunners_ReturnsNothing()
    {
        Assert.Empty(await CreateBackend().DiscoverManagedProcessesAsync([HostBasePath]));

        Directory.CreateDirectory(DefaultBasePath);
        Directory.CreateDirectory(HostBasePath);
        Assert.Empty(await CreateBackend().DiscoverManagedProcessesAsync([HostBasePath]));
    }

    // Guard: a PID file naming a process that is gone is still reported, as exited.
    [Fact]
    public async Task Discover_RunnerWhoseProcessIsGone_IsReportedExited()
    {
        WriteInstance(DefaultBasePath, "rr-gone", "inst-gone", "Macos-jit-gone", int.MaxValue);

        var runner = Assert.Single(await CreateBackend().DiscoverManagedProcessesAsync());

        Assert.Equal("inst-gone", runner.InstanceId);
        Assert.False(runner.IsRunning);
        Assert.Equal("exited", runner.Status);
    }

    // A PID file that holds no PID was skipped like a runner that never started, so a live
    // runner whose PID file was garbled was missing from the report, read as exited.
    [Fact]
    public async Task Discover_WhenAPidFileIsMalformed_Throws()
    {
        WriteInstance(DefaultBasePath, "rr-live", "inst-live", "Macos-jit-live", Environment.ProcessId);
        WriteInstance(DefaultBasePath, "rr-garbled", "inst-garbled", "Macos-jit-garbled", Environment.ProcessId);
        await File.WriteAllTextAsync(Path.Combine(DefaultBasePath, "instances", "rr-garbled", "rr.pid"), "12a4");

        await Assert.ThrowsAnyAsync<Exception>(() => CreateBackend().DiscoverManagedProcessesAsync());
    }

    // Guard: a deploy writes its instance files before it launches the runner, and a deploy
    // that failed leaves them behind. No PID file means nothing was started from there.
    [Fact]
    public async Task Discover_InstanceWithoutAPidFile_IsSkipped()
    {
        WriteInstance(DefaultBasePath, "rr-live", "inst-live", "Macos-jit-live", Environment.ProcessId);
        WriteInstance(DefaultBasePath, "rr-unstarted", "inst-unstarted", "Macos-jit-unstarted", Environment.ProcessId);
        File.Delete(Path.Combine(DefaultBasePath, "instances", "rr-unstarted", "rr.pid"));

        var runner = Assert.Single(await CreateBackend().DiscoverManagedProcessesAsync());

        Assert.Equal("inst-live", runner.InstanceId);
    }

    // The Agent and the HostWorker (or two HostWorker versions during an upgrade) each record
    // the base paths their deploys use. Every recorded path must survive, whichever
    // instance recorded it.
    [Fact]
    public async Task Discover_FindsRunnersUnderPathsRecordedByDifferentBackends()
    {
        var first = Path.Combine(_root, "first-base-path");
        var second = Path.Combine(_root, "second-base-path");
        CreateBackend().RecordRunnerBasePath(first);
        CreateBackend().RecordRunnerBasePath(second);
        WriteInstance(first, "rr-first", "inst-first", "Macos-jit-first", Environment.ProcessId);
        WriteInstance(second, "rr-second", "inst-second", "Macos-jit-second", Environment.ProcessId);

        var runners = await CreateBackend().DiscoverManagedProcessesAsync();

        Assert.Equal(["inst-first", "inst-second"], runners.Select(r => r.InstanceId).Order());
    }

    // Separate processes share no lock. The recorded paths lived in one file that each
    // recorder read, extended and rewrote, so two processes recording at once lost each
    // other's paths, and a runner under a lost path was reported gone while it ran. Each
    // NativeBackend here comes from its own AssemblyLoadContext, so like a separate process
    // it has its own statics (and its own copy of any lock).
    [Fact]
    public async Task RecordRunnerBasePath_FromSeparateProcessesAtOnce_KeepsEveryPath()
    {
        const int pathsPerRecorder = 40;
        var contexts = new[]
        {
            new AssemblyLoadContext("native-recorder-a", isCollectible: true),
            new AssemblyLoadContext("native-recorder-b", isCollectible: true)
        };
        try
        {
            var recorders = contexts.Select(CreateBackendInContext).ToArray();
            var paths = Enumerable.Range(0, recorders.Length)
                .Select(r => Enumerable.Range(0, pathsPerRecorder)
                    .Select(i => Path.Combine(_root, $"recorder-{r}-path-{i}"))
                    .ToArray())
                .ToArray();
            using var start = new Barrier(recorders.Length);

            await Task.WhenAll(recorders.Select((record, r) => Task.Run(() =>
            {
                start.SignalAndWait();
                foreach (var path in paths[r])
                    record(path);
            })));

            var expected = new List<string>();
            foreach (var path in paths.SelectMany(p => p))
            {
                var instanceId = "inst-" + Path.GetFileName(path);
                WriteInstance(path, "rr-" + Path.GetFileName(path), instanceId, "Macos-jit-" + Path.GetFileName(path), Environment.ProcessId);
                expected.Add(instanceId);
            }

            var runners = await CreateBackend().DiscoverManagedProcessesAsync();

            Assert.Equal(expected.Order(), runners.Select(r => r.InstanceId).Order());
        }
        finally
        {
            foreach (var context in contexts)
                context.Unload();
        }
    }

    // Each recorded path is its own marker file, written without reading the others, so
    // one recorder cannot overwrite another's path and a broken marker cannot stop a deploy
    // from recording its own. Discovery still refuses to guess past the broken one.
    [Fact]
    public async Task RecordRunnerBasePath_WritesOneMarkerPerPath_WithoutReadingTheOthers()
    {
        var markerDir = Path.Combine(DefaultBasePath, "native-base-paths");
        Directory.CreateDirectory(markerDir);
        await File.WriteAllTextAsync(Path.Combine(markerDir, "0000000000000000.path"), "");
        var first = Path.Combine(_root, "first-base-path");
        var second = Path.Combine(_root, "second-base-path");

        CreateBackend().RecordRunnerBasePath(first);
        CreateBackend().RecordRunnerBasePath(second + Path.DirectorySeparatorChar);
        CreateBackend().RecordRunnerBasePath(first);

        var markers = Directory.GetFiles(markerDir)
            .Where(f => Path.GetFileName(f) != "0000000000000000.path")
            .Select(File.ReadAllText)
            .Order()
            .ToArray();
        Assert.Equal([first, second], markers);
        Assert.Empty(Directory.GetFiles(markerDir, "*.tmp"));
        await Assert.ThrowsAnyAsync<Exception>(() => CreateBackend().DiscoverManagedProcessesAsync());
    }

    // The launched runner's PID is written after it starts. When that write failed, the
    // runner kept running with no PID file, so discovery never reported it and nothing would
    // ever stop it: a runner that could take a job no one would see.
    [Fact]
    public async Task Start_WhenThePidFileCannotBeWritten_StopsTheLaunchedRunner()
    {
        if (OperatingSystem.IsWindows())
            return;

        var token = await CreateSpawningAgentAsync();
        // The instance is a copy of the agent directory, so this makes rr.pid a directory there.
        var agentDir = Path.Combine(HostBasePath, "agents", "githubactions", "2.999.0");
        Directory.CreateDirectory(Path.Combine(agentDir, "rr.pid"));
        await File.WriteAllTextAsync(Path.Combine(agentDir, "rr.pid", "placeholder"), "");

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => StartSpawningRunnerAsync(CreateBackend(), token));

            Assert.Empty(FindProcessesWithArgument(token));
        }
        finally
        {
            KillProcessesWithArgument(token);
        }
    }

    // A PID file write that fails part way (a full disk) left an empty or partial rr.pid
    // behind after the runner was stopped. Discovery reads every rr.pid as a live runner's,
    // so it failed the scan on every report from then on: the host reported the native
    // backend unavailable, hiding every native runner on it, until someone removed the file.
    [Fact]
    public async Task Start_WhenThePidFileWriteFillsTheDisk_LeavesNothingThatBreaksDiscovery()
    {
        if (OperatingSystem.IsWindows())
            return;

        var token = await CreateSpawningAgentAsync();
        var backend = new NativeBackend(NullLogger<NativeBackend>.Instance)
        {
            DefaultBasePath = DefaultBasePath,
            WriteTextFileAsync = async (path, _, _) =>
            {
                await File.WriteAllTextAsync(path, "");
                throw new IOException("No space left on device");
            }
        };

        try
        {
            var error = await Assert.ThrowsAsync<IOException>(() => StartSpawningRunnerAsync(backend, token));
            Assert.Equal("No space left on device", error.Message);
            Assert.Empty(FindProcessesWithArgument(token));

            Assert.Empty(await CreateBackend().DiscoverManagedProcessesAsync());
            Assert.Empty(Directory.GetFiles(SpawningInstanceDir, "rr.pid*"));
        }
        finally
        {
            KillProcessesWithArgument(token);
        }
    }

    // Whatever rr.pid the instance holds when the write fails names no runner of ours, since
    // the one launched was stopped, so it is removed rather than left for discovery.
    [Fact]
    public async Task Start_WhenThePidFileCannotBeWritten_RemovesTheRrPidLeftInTheInstance()
    {
        if (OperatingSystem.IsWindows())
            return;

        var token = await CreateSpawningAgentAsync();
        // A stray PID file in the cached agent is copied into the instance.
        await File.WriteAllTextAsync(Path.Combine(HostBasePath, "agents", "githubactions", "2.999.0", "rr.pid"), "");
        var backend = new NativeBackend(NullLogger<NativeBackend>.Instance)
        {
            DefaultBasePath = DefaultBasePath,
            WriteTextFileAsync = (_, _, _) => throw new UnauthorizedAccessException("Access to the path is denied.")
        };

        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => StartSpawningRunnerAsync(backend, token));
            Assert.Empty(FindProcessesWithArgument(token));

            Assert.Empty(await CreateBackend().DiscoverManagedProcessesAsync());
            Assert.False(File.Exists(Path.Combine(SpawningInstanceDir, "rr.pid")));
            Assert.True(Directory.Exists(SpawningInstanceDir));
        }
        finally
        {
            KillProcessesWithArgument(token);
        }
    }

    // When the PID file write failed, rr.pid was removed even when the kill did not confirm
    // that the runner exited. A runner still running then had no PID file, so discovery never
    // reported it and nothing would stop it, though it could take a job. rr.pid is removed
    // only once the runner has exited.
    [Fact]
    public async Task Start_WhenThePidFileWriteFailsAndTheKillIsNotConfirmed_KeepsTheRrPid()
    {
        if (OperatingSystem.IsWindows())
            return;

        var token = await CreateSpawningAgentAsync();
        string? launchedPid = null;
        var backend = new NativeBackend(NullLogger<NativeBackend>.Instance)
        {
            DefaultBasePath = DefaultBasePath,
            // A write reported failed after the PID reached rr.pid.
            WriteTextFileAsync = async (_, contents, _) =>
            {
                launchedPid = contents;
                await File.WriteAllTextAsync(Path.Combine(SpawningInstanceDir, "rr.pid"), contents);
                throw new IOException("Input/output error");
            },
            KillProcessTreeAndWaitForExit = (_, _) => false
        };

        try
        {
            var error = await Assert.ThrowsAsync<IOException>(() => StartSpawningRunnerAsync(backend, token));
            Assert.Equal("Input/output error", error.Message);
            Assert.NotNull(launchedPid);
            var pid = int.Parse(launchedPid, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Contains(pid, FindProcessesWithArgument(token));

            Assert.Equal(launchedPid, await File.ReadAllTextAsync(Path.Combine(SpawningInstanceDir, "rr.pid")));
            var runner = Assert.Single(await CreateBackend().DiscoverManagedProcessesAsync());
            Assert.Equal(SpawningInstanceId, runner.InstanceId);
            Assert.Equal(pid, runner.ProcessId);
            Assert.True(runner.IsRunning);
        }
        finally
        {
            KillProcessesWithArgument(token);
        }
    }

    // A runner the kill could not stop is kept discoverable even when the failed write left
    // no rr.pid: the PID is written once more.
    [Fact]
    public async Task Start_WhenThePidFileWriteFailsAndTheKillFails_WritesTheRrPidAgain()
    {
        if (OperatingSystem.IsWindows())
            return;

        var token = await CreateSpawningAgentAsync();
        var writes = 0;
        var backend = new NativeBackend(NullLogger<NativeBackend>.Instance)
        {
            DefaultBasePath = DefaultBasePath,
            WriteTextFileAsync = async (path, contents, ct) =>
            {
                if (Interlocked.Increment(ref writes) == 1)
                {
                    await File.WriteAllTextAsync(path, "", ct);
                    throw new IOException("No space left on device");
                }
                await File.WriteAllTextAsync(path, contents, ct);
            },
            KillProcessTreeAndWaitForExit = (_, _) => throw new InvalidOperationException("Could not kill the process tree")
        };

        try
        {
            var error = await Assert.ThrowsAsync<IOException>(() => StartSpawningRunnerAsync(backend, token));
            Assert.Equal("No space left on device", error.Message);

            var runner = Assert.Single(await CreateBackend().DiscoverManagedProcessesAsync());
            Assert.Equal(SpawningInstanceId, runner.InstanceId);
            Assert.True(runner.IsRunning);
            Assert.Contains(runner.ProcessId!.Value, FindProcessesWithArgument(token));
            Assert.Equal(
                runner.ProcessId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                await File.ReadAllTextAsync(Path.Combine(SpawningInstanceDir, "rr.pid")));
            Assert.Empty(Directory.GetFiles(SpawningInstanceDir, "rr.pid.*.tmp"));
        }
        finally
        {
            KillProcessesWithArgument(token);
        }
    }

    // The PID file is written to a temporary name and renamed into place, so a reader sees a
    // whole PID or none. The temporary file names no runner.
    [Fact]
    public async Task Discover_IgnoresAnInstanceWithOnlyATemporaryPidFile()
    {
        var dir = Path.Combine(DefaultBasePath, "instances", "rr-writing");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(
            Path.Combine(dir, "rr-instance.json"),
            JsonSerializer.Serialize(new { InstanceId = "inst-writing", RunnerName = "Macos-jit-writing" }));
        await File.WriteAllTextAsync(Path.Combine(dir, $"rr.pid.{Guid.NewGuid():N}.tmp"), "36");

        Assert.Empty(await CreateBackend().DiscoverManagedProcessesAsync());
    }

    private const string SpawningInstanceId = "inst-pid-write";
    private const string SpawningRunnerName = "Macos-jit-pid-write";

    private string SpawningInstanceDir => Path.Combine(
        HostBasePath, "instances", NativeBackend.CreateSafeRunnerDirectoryName(SpawningRunnerName, SpawningInstanceId));

    // A cached GitHub Actions agent under HostBasePath whose run.sh starts a runner and a
    // child that both carry the returned token in their arguments, so both are visible to ps.
    private async Task<string> CreateSpawningAgentAsync()
    {
        var token = "rr-pid-write-test-" + Guid.NewGuid().ToString("N");
        var agentDir = Path.Combine(HostBasePath, "agents", "githubactions", "2.999.0");
        Directory.CreateDirectory(agentDir);
        await File.WriteAllTextAsync(Path.Combine(agentDir, "config.sh"), "#!/bin/bash\nexit 0\n");
        await File.WriteAllTextAsync(
            Path.Combine(agentDir, "run.sh"),
            "#!/bin/bash\nbash -c 'sleep 120 & wait' rr-runner-child \"$@\" &\nwait\n");
        return token;
    }

    private Task<RunnerInstanceInfo> StartSpawningRunnerAsync(NativeBackend backend, string token) =>
        backend.StartRunnerAsync(new RunnerStartRequest
        {
            InstanceId = SpawningInstanceId,
            RunnerName = SpawningRunnerName,
            Provider = RunnerProvider.GitHubActions,
            RunnerAgentVersion = "2.999.0",
            RunnerBasePath = HostBasePath,
            JitConfig = token
        });

    private static void KillProcessesWithArgument(string argument)
    {
        foreach (var pid in FindProcessesWithArgument(argument))
        {
            try
            {
                using var leaked = Process.GetProcessById(pid);
                leaked.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
            }
        }
    }

    private static List<int> FindProcessesWithArgument(string argument)
    {
        var psi = new ProcessStartInfo("ps", "-A -o pid= -o args=")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        using var ps = Process.Start(psi)!;
        var output = ps.StandardOutput.ReadToEnd();
        ps.WaitForExit();
        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains(argument, StringComparison.Ordinal))
            .Select(line => int.Parse(line.Trim().Split(' ', 2)[0], System.Globalization.CultureInfo.InvariantCulture))
            .Where(pid => pid != ps.Id)
            .ToList();
    }

    // A NativeBackend from its own copy of the Agent assembly, returned as its
    // RecordRunnerBasePath. Dependencies (Core, logging) come from the default context.
    private Action<string> CreateBackendInContext(AssemblyLoadContext context)
    {
        var assembly = context.LoadFromAssemblyPath(typeof(NativeBackend).Assembly.Location);
        var backendType = assembly.GetType(typeof(NativeBackend).FullName!, throwOnError: true)!;
        Assert.NotSame(typeof(NativeBackend), backendType);
        var loggerType = typeof(NullLogger<>).MakeGenericType(backendType);
        var logger = loggerType.GetField(nameof(NullLogger<object>.Instance), BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
        var backend = Activator.CreateInstance(backendType, logger)!;
        backendType.GetProperty(nameof(NativeBackend.DefaultBasePath), BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(backend, DefaultBasePath);
        var record = backendType.GetMethod(nameof(NativeBackend.RecordRunnerBasePath), BindingFlags.NonPublic | BindingFlags.Instance)!;
        return path =>
        {
            try
            {
                record.Invoke(backend, [path]);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Throw(ex.InnerException);
            }
        };
    }

    private NativeBackend CreateBackend() =>
        new(NullLogger<NativeBackend>.Instance) { DefaultBasePath = DefaultBasePath };

    // A cached agent without config scripts makes StartRunnerAsync throw after it has picked
    // the base path but before it downloads or deploys anything.
    private static async Task StartUntilTheAgentCheckAsync(NativeBackend backend, string basePath)
    {
        var agentDir = Path.Combine(basePath, "agents", "githubactions", "2.999.0");
        Directory.CreateDirectory(agentDir);
        await File.WriteAllTextAsync(Path.Combine(agentDir, "placeholder"), "");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => backend.StartRunnerAsync(new RunnerStartRequest
        {
            InstanceId = "inst-live",
            RunnerName = "Macos-jit-live",
            Provider = RunnerProvider.GitHubActions,
            RunnerAgentVersion = "2.999.0",
            RunnerBasePath = basePath
        }));
        Assert.Contains("missing config scripts", error.Message);
    }

    internal static void WriteInstance(string basePath, string directoryName, string instanceId, string runnerName, int pid)
    {
        var dir = Path.Combine(basePath, "instances", directoryName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "rr-instance.json"),
            JsonSerializer.Serialize(new { InstanceId = instanceId, RunnerName = runnerName }));
        File.WriteAllText(Path.Combine(dir, "rr.pid"), pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
