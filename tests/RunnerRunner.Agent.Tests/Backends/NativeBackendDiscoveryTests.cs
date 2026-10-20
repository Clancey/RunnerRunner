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
