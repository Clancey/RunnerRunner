using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using Orleans.TestingHost;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Data;
using RunnerRunner.Server.Grains.Interfaces;
using RunnerRunner.Server.Services;
using RunnerRunner.Server.Tests.TestSupport;
using Shiny.DocumentDb;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Tests.Grains;

public sealed class HostRegistrationRecoveryTests
{
    [Fact]
    public async Task Register_MigrationFailureIsReportedAndRetryPreservesConfigurationAndReferences()
    {
        // Deliberately omit production initialization in this isolated cluster.
        // Do not remove tables or inject failures into the shared grain fixture.
        var store = TestDocumentStore.Create();
        SiloConfigurator.DocumentStore = store;
        var builder = new TestClusterBuilder();
        builder.Options.InitialSilosCount = 1;
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        using var cluster = builder.Build();
        try
        {
            cluster.Deploy();
            var oldHostId = OrleansTestIds.Create("legacy");
            var newHostId = OrleansTestIds.Create("worker");
            var hostName = OrleansTestIds.Create("host-name");
            await store.Insert(new Host
            {
                Id = oldHostId, Name = hostName, Platform = HostPlatform.MacOS,
                MaxDockerContainers = 0, MaxTartVMs = 2, MaxNativeProcesses = 0,
                BackendLimitsConfigured = true, GroupId = "saved-group",
                Labels = new() { ["rack"] = "a" }, IsApproved = true
            });
            var instance = new RunnerInstance
            {
                Id = OrleansTestIds.Create("instance"), HostId = oldHostId,
                ProfileId = "profile", RunnerName = "runner"
            };
            await store.Insert(instance);

            var grain = cluster.GrainFactory.GetGrain<IHostGrain>(newHostId);
            var failure = await Assert.ThrowsAsync<SqliteException>(() =>
                grain.Register(hostName, HostPlatform.MacOS, "arm64", "1.0.0"));
            Assert.Contains("no such table: runner_assignments", failure.Message);
            Assert.NotNull(await store.Get<Host>(oldHostId));
            Assert.Equal(oldHostId, (await store.Get<RunnerInstance>(instance.Id))!.HostId);
            var saved = (await store.Get<Host>(newHostId))!;
            Assert.Equal((0, 2, 0, "saved-group"),
                (saved.MaxDockerContainers, saved.MaxTartVMs, saved.MaxNativeProcesses, saved.GroupId));
            Assert.True(saved.BackendLimitsConfigured);

            // Repair the actual cause, then retry the same grain/identity. This
            // covers recovery after the new document was inserted but migration
            // failed, not just the successful first-registration branch.
            using (var services = new ServiceCollection().AddSingleton(store).BuildServiceProvider())
                await DatabaseInitializer.EnsureTablesCreatedAsync(services);
            var assignment = new RunnerAssignment { Id = OrleansTestIds.Create("assignment"), HostId = oldHostId };
            var image = new AgentImage
            {
                Id = OrleansTestIds.Create("image"), HostId = oldHostId, Repository = "test-image"
            };
            var rule = new ProvisioningRule
            {
                Id = OrleansTestIds.Create("rule"), Name = "test-rule", TargetHostId = oldHostId
            };
            await store.Insert(assignment);
            await store.Insert(image);
            await store.Insert(rule);

            await grain.Register(hostName, HostPlatform.MacOS, "arm64", "1.0.0");
            await grain.RecordHeartbeat("recovered", 0, null);

            Assert.Null(await store.Get<Host>(oldHostId));
            Assert.Equal(newHostId, (await store.Get<RunnerAssignment>(assignment.Id))!.HostId);
            Assert.Equal(newHostId, (await store.Get<RunnerInstance>(instance.Id))!.HostId);
            Assert.Equal(newHostId, (await store.Get<AgentImage>(image.Id))!.HostId);
            Assert.Equal(newHostId, (await store.Get<ProvisioningRule>(rule.Id))!.TargetHostId);
            var state = await grain.GetState();
            Assert.Equal((0, 2, 0, "saved-group"),
                (state.MaxDockerContainers, state.MaxTartVMs, state.MaxNativeProcesses, state.GroupId));
            Assert.True(state.BackendLimitsConfigured);
            Assert.Equal("a", state.Labels["rack"]);
            Assert.False(await grain.CanAcceptRunner(ExecutionBackend.Native));
            Assert.False(await grain.CanAcceptRunner(ExecutionBackend.Docker));
            Assert.True(await grain.CanAcceptRunner(ExecutionBackend.Tart));
            saved = (await store.Get<Host>(newHostId))!;
            Assert.Equal((0, 2, 0), (saved.MaxDockerContainers, saved.MaxTartVMs, saved.MaxNativeProcesses));
        }
        finally
        {
            cluster.StopAllSilos();
            SiloConfigurator.DocumentStore = null;
        }
    }

    private sealed class SiloConfigurator : ISiloConfigurator
    {
        internal static IDocumentStore? DocumentStore { get; set; }

        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder
                .AddMemoryGrainStorage("PersistentStore")
                .AddMemoryGrainStorage("PubSubStore")
                .AddMemoryStreams("RunnerEvents")
                .ConfigureServices(services =>
                {
                    services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
                    services.AddSingleton(DocumentStore ?? throw new InvalidOperationException("Test store not configured."));
                });
        }
    }
}
