using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using Orleans.TestingHost;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Data;
using RunnerRunner.Server.Grains;
using RunnerRunner.Server.Grains.Interfaces;
using RunnerRunner.Server.Grains.State;
using RunnerRunner.Server.Services;
using RunnerRunner.Server.Tests.TestSupport;
using Shiny.DocumentDb;

namespace RunnerRunner.Server.Tests.Grains;

[CollectionDefinition(Name)]
public sealed class ShortHealthTimeoutClusterCollection : ICollectionFixture<ShortHealthTimeoutClusterFixture>
{
    public const string Name = "Orleans cluster (short health timeout)";
}

/// <summary>
/// A separate cluster whose runner grains declare health stale after one second and hit the
/// dynamic job timeout after two, so those timers can fire inside a test. Kept apart from <see cref="OrleansTestClusterFixture"/>
/// so the short timeout cannot leak into other grain tests.
/// </summary>
public sealed class ShortHealthTimeoutClusterFixture : IDisposable
{
    internal static readonly TimeSpan HealthStaleTimeout = TimeSpan.FromSeconds(1);

    internal static readonly TimeSpan DynamicJobTimeout = TimeSpan.FromSeconds(2);

    public ShortHealthTimeoutClusterFixture()
    {
        using (var services = new ServiceCollection().AddSingleton(DocumentStore).BuildServiceProvider())
            DatabaseInitializer.EnsureTablesCreatedAsync(services).GetAwaiter().GetResult();

        var builder = new TestClusterBuilder();
        builder.Options.InitialSilosCount = 1;
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();

        Cluster = builder.Build();
        Cluster.Deploy();
    }

    public TestCluster Cluster { get; }

    public IGrainFactory GrainFactory => Cluster.GrainFactory;

    public IDocumentStore DocumentStore => SiloConfigurator.DocumentStore;

    internal RecordingHostCommandDispatcher HostCommands => SiloConfigurator.HostCommands;

    public void Dispose() => Cluster.StopAllSilos();

    private sealed class SiloConfigurator : ISiloConfigurator
    {
        internal static IDocumentStore DocumentStore { get; } = TestDocumentStore.Create();

        internal static RecordingHostCommandDispatcher HostCommands { get; } = new();

        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder
                .AddMemoryGrainStorage("Default")
                .AddMemoryGrainStorage("PersistentStore")
                .AddMemoryGrainStorage("PubSubStore")
                .AddMemoryStreams("RunnerEvents")
                .UseInMemoryReminderService()
                .ConfigureServices(services =>
                {
                    services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
                    services.AddSingleton(DocumentStore);
                    services.AddSingleton(HostCommands);
                    services.AddSingleton<IHostCommandDispatcher>(sp => sp.GetRequiredService<RecordingHostCommandDispatcher>());
                    services.AddSingleton<RunnerRegistrationCleanupService>();
                    services.AddSingleton(new RunnerInstanceGrainOptions
                    {
                        HealthStaleTimeout = HealthStaleTimeout,
                        DynamicJobTimeout = DynamicJobTimeout
                    });
                });
        }
    }
}

[Collection(ShortHealthTimeoutClusterCollection.Name)]
public sealed class RunnerInstanceStaleHealthTests
{
    private readonly ShortHealthTimeoutClusterFixture _fixture;

    public RunnerInstanceStaleHealthTests(ShortHealthTimeoutClusterFixture fixture)
    {
        _fixture = fixture;
    }

    // Reproduces M4ini 2026-10-20: the host slept 11:48:19-11:51:16, so no health reports
    // arrived, and at 11:50:56 the grain declared instances 819c357d (native PID 3651,
    // LogicLand job 118606912280) and 02b717c2 (Tart VM rr-FashionPact-macOS-VM-jit-44578a61,
    // FashionPactPos job 118535576689) crashed. Crashing purged the dynamic grain; the next
    // host event rewrote the record with a blank host and runner name, and reconciliation
    // then cleaned up both live resources as orphans. Both jobs died.
    [Fact]
    public async Task StaleHealth_KeepsDynamicRunnerRunningAndItsRecordIntact()
    {
        var (runner, instanceId, hostId) = await StartDynamicTartRunnerAsync("rr-FashionPact-macOS-VM-jit-44578a61");

        var state = await WaitForStaleMessageAsync(runner);

        Assert.Equal(RunnerInstanceStatus.Running, state.Status);
        Assert.Equal(hostId, state.HostId);
        Assert.Equal("FashionPact-macOS-VM-jit-44578a61", state.RunnerName);
        Assert.Equal("rr-FashionPact-macOS-VM-jit-44578a61", state.VmName);

        var document = await _fixture.DocumentStore.Get<RunnerInstance>(instanceId);
        Assert.NotNull(document);
        Assert.Equal(RunnerInstanceStatus.Running, document!.Status);
        Assert.Equal(hostId, document.HostId);
        Assert.Equal("FashionPact-macOS-VM-jit-44578a61", document.RunnerName);
        Assert.StartsWith(RunnerInstanceGrain.HealthStaleMessagePrefix, document.StatusMessage);

        Assert.DoesNotContain(_fixture.HostCommands.Commands, c => c.Command is StopRunnerCommand stop && stop.InstanceId == instanceId);
    }

    [Fact]
    public async Task StaleHealth_ClearsOnceTheHostReportsTheRunnerAgain()
    {
        var (runner, _, _) = await StartDynamicTartRunnerAsync("rr-stale-then-healthy");

        await WaitForStaleMessageAsync(runner);
        await runner.UpdateHealth();

        var state = await runner.GetState();
        Assert.Equal(RunnerInstanceStatus.Running, state.Status);
        Assert.Equal("Running", state.StatusMessage);
    }

    // Reproduces tower-docker 2026-10-20 13:49:35: two hours after instance 9ac7f9f7
    // (rr-ll-linux-jit-e38466ec) started, the grain logged "Dynamic runner timeout — no
    // completion webhook received within 2 hours" and failed it while its container was
    // running and reporting health. Five seconds later reconciliation removed the running
    // container as an orphan (exit 137). Webhooks were down, so the missing completion
    // webhook said nothing about whether the runner was building.
    [Fact]
    public async Task DynamicJobTimeout_DoesNotFailAHealthyRunner()
    {
        var (runner, instanceId, hostId) = await StartDynamicTartRunnerAsync("rr-ll-linux-jit-e38466ec");

        var until = DateTime.UtcNow + ShortHealthTimeoutClusterFixture.DynamicJobTimeout + TimeSpan.FromSeconds(1.5);
        while (DateTime.UtcNow < until)
        {
            await runner.UpdateHealth();
            await Task.Delay(200);
        }

        var state = await runner.GetState();
        Assert.Equal(RunnerInstanceStatus.Running, state.Status);
        Assert.Equal(hostId, state.HostId);

        var document = await _fixture.DocumentStore.Get<RunnerInstance>(instanceId);
        Assert.NotNull(document);
        Assert.Equal(RunnerInstanceStatus.Running, document!.Status);
        Assert.Equal(hostId, document.HostId);
        Assert.Equal("ll-linux-jit-e38466ec", document.RunnerName);

        Assert.DoesNotContain(_fixture.HostCommands.Commands, c => c.Command is StopRunnerCommand stop && stop.InstanceId == instanceId);
    }

    private async Task<(IRunnerInstanceGrain Runner, string InstanceId, string HostId)> StartDynamicTartRunnerAsync(string vmName)
    {
        var hostId = OrleansTestIds.Create("host");
        var profileId = OrleansTestIds.Create("profile");
        var instanceId = OrleansTestIds.Create("runner");

        var host = _fixture.GrainFactory.GetGrain<IHostGrain>(hostId);
        await host.Register("M4ini", HostPlatform.MacOS, "arm64", "1.0.0");

        var profile = _fixture.GrainFactory.GetGrain<IProfileGrain>(profileId);
        await profile.SetProfile(new RunnerProfile
        {
            Id = profileId,
            Name = "FashionPact-macOS-VM",
            ExecutionBackend = ExecutionBackend.Tart
        });

        var runner = _fixture.GrainFactory.GetGrain<IRunnerInstanceGrain>(instanceId);
        await runner.Initialize(hostId, profileId, vmName["rr-".Length..], "dynamic", jobId: "118535576689");
        await runner.MarkStarting("Deploying");
        await runner.MarkRunning(vmName: vmName, statusMessage: "Running");
        return (runner, instanceId, hostId);
    }

    private static async Task<RunnerInstanceGrainState> WaitForStaleMessageAsync(IRunnerInstanceGrain runner)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            var state = await runner.GetState();
            if (state.Status != RunnerInstanceStatus.Running
                || state.StatusMessage?.StartsWith(RunnerInstanceGrain.HealthStaleMessagePrefix, StringComparison.Ordinal) == true)
            {
                return state;
            }

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The health-stale timer never fired.");

            await Task.Delay(100);
        }
    }
}
