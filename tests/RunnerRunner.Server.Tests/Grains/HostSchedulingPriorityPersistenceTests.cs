using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains.Interfaces;
using RunnerRunner.Server.Services;
using RunnerRunner.Server.Services.HostWorkers;
using RunnerRunner.Server.Tests.TestSupport;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Tests.Grains;

[Collection(OrleansClusterCollection.Name)]
public sealed class HostSchedulingPriorityPersistenceTests(OrleansTestClusterFixture fixture)
{
    [Theory]
    [InlineData(10)]
    [InlineData(0)]
    [InlineData(-10)]
    public async Task PrioritySurvivesDocumentReloadGrainHeartbeatsAndWorkerReconnect(int priority)
    {
        var hostId = OrleansTestIds.Create("priority-host");
        await fixture.DocumentStore.Insert(new Host { Id = hostId, Name = hostId });
        var host = fixture.GrainFactory.GetGrain<IHostGrain>(hostId);
        await host.Register(hostId, HostPlatform.MacOS, "arm64", "test");
        await host.SetResourceLimits(maxDocker: 0, maxTart: 2, maxNative: 4);
        await host.RecordHeartbeat("initial", 0, null);
        var document = await fixture.DocumentStore.Get<Host>(hostId);
        Assert.NotNull(document);
        document.SchedulingPriority = priority;
        await fixture.DocumentStore.Update(document);
        Assert.Equal(priority, (await fixture.DocumentStore.Get<Host>(hostId))!.SchedulingPriority);

        await host.RecordHeartbeat("heartbeat", 1, new HostResourceUsage { RunningTartVmCount = 1 });
        await host.MarkOffline();
        using var tasks = new LongRunningTaskService(NullLogger<LongRunningTaskService>.Instance);
        var processor = new HostWorkerEventProcessor(
            fixture.DocumentStore,
            fixture.GrainFactory,
            new HostWorkerLogCache(new ConfigurationBuilder().Build()),
            tasks,
            new ConfigurationBuilder().Build(),
            NullLogger<HostWorkerEventProcessor>.Instance);

        var resolvedId = await processor.WorkerConnectedAsync(
            new AgentInfo
            {
                AgentId = hostId,
                Name = hostId,
                Platform = HostPlatform.MacOS,
                Architecture = "arm64",
                Capabilities = ["native", "tart"]
            },
            "reconnected",
            new Dictionary<string, string> { ["native"] = "true", ["tart"] = "true" },
            null,
            CancellationToken.None);
        await host.RecordHeartbeat("after-reconnect", 1, null);

        var reloaded = await fixture.DocumentStore.Get<Host>(resolvedId);
        Assert.NotNull(reloaded);
        Assert.Equal(hostId, resolvedId);
        Assert.Equal(priority, reloaded.SchedulingPriority);
        Assert.Equal(AgentStatus.Online, reloaded.AgentStatus);
        Assert.Equal(0, reloaded.MaxDockerContainers);
        Assert.Equal(2, reloaded.MaxTartVMs);
        Assert.Equal(4, reloaded.MaxNativeProcesses);
    }
}
