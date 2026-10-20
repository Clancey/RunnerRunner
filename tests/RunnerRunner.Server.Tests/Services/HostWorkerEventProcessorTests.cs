using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RunnerRunner.Core.HostWorkers;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains.Interfaces;
using RunnerRunner.Server.Services;
using RunnerRunner.Server.Services.HostWorkers;
using Shiny.DocumentDb;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Tests.Services;

public class HostWorkerEventProcessorTests
{
    [Fact]
    public async Task HandleMessageAsync_IngestsLogFramesIntoRecentCache()
    {
        var cache = CreateCache();
        var processor = CreateProcessor(cache);
        var message = HostWorkerProtocol.CreateMessage(
            "worker-host",
            HostWorkerMessageKinds.LogFrame,
            new HostWorkerLogFrame
            {
                StreamKind = "worker.command",
                StreamId = "command-1",
                Offset = 0,
                Text = "accepted\n",
                Timestamp = DateTimeOffset.UnixEpoch
            });

        await processor.HandleMessageAsync("canonical-host", message, CancellationToken.None);

        Assert.Equal("accepted\n", cache.GetTextTail("canonical-host", "command-1", maxFrames: 10));
    }

    [Fact]
    public async Task HandleMessageAsync_DoesNotThrowForMalformedWorkerMessage()
    {
        var cache = CreateCache();
        var processor = CreateProcessor(cache);
        var message = new HostWorkerMessage
        {
            HostId = "worker-host",
            Kind = HostWorkerMessageKinds.LogFrame,
            PayloadJson = "{not-json"
        };

        await processor.HandleMessageAsync("canonical-host", message, CancellationToken.None);

        Assert.Empty(cache.GetTail("canonical-host", "command-1", maxFrames: 10));
    }

    [Fact]
    public async Task HandleMessageAsync_SendsHeartbeatUsageToHostGrainWithoutRewritingDocument()
    {
        var store = TestDocumentStore.Create();
        await store.Insert(new Host
        {
            Id = "canonical-host",
            Name = "mac-mini",
            Platform = HostPlatform.MacOS
        });
        var hostGrain = Substitute.For<IHostGrain>();
        var processor = CreateProcessor(store, hostGrain: hostGrain);

        await processor.HandleMessageAsync("canonical-host", CreateHeartbeatMessage(runningTartVms: 2), CancellationToken.None);

        await hostGrain.Received(1).RecordHeartbeat(
            "canonical-host", 0, Arg.Is<HostResourceUsage?>(u => u != null && u.RunningTartVmCount == 2));
        // The grain owns the document write. A read-modify-write here could land a stale
        // copy of the backend limits on top of a concurrent Hosts page save.
        var host = await store.Get<Host>("canonical-host");
        Assert.Null(host!.ObservedResourceUsageAt);
        Assert.Equal(default, host.LastHeartbeat);
    }

    [Fact]
    public async Task HandleMessageAsync_StoresObservedTartUsageWhenHostGrainIsUnavailable()
    {
        var store = TestDocumentStore.Create();
        await store.Insert(new Host
        {
            Id = "canonical-host",
            Name = "mac-mini",
            Platform = HostPlatform.MacOS
        });
        var hostGrain = Substitute.For<IHostGrain>();
        hostGrain.RecordHeartbeat(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<HostResourceUsage?>())
            .Returns(Task.FromException(new InvalidOperationException("silo unavailable")));
        var processor = CreateProcessor(store, hostGrain: hostGrain);

        await processor.HandleMessageAsync("canonical-host", CreateHeartbeatMessage(runningTartVms: 2), CancellationToken.None);

        var host = await store.Get<Host>("canonical-host");
        Assert.NotNull(host);
        Assert.Equal(2, host.ObservedRunningTartVMs);
        Assert.NotNull(host.ObservedResourceUsageAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeWrite_DoesNotRevertConcurrentConfigurationSave(bool heartbeatFallback)
    {
        var store = TestDocumentStore.Create();
        await store.Insert(new Host
        {
            Id = "worker-1",
            Name = "mac-mini",
            Platform = HostPlatform.MacOS,
            IsApproved = true,
            MaxDockerContainers = 10,
            MaxTartVMs = 0,
            MaxNativeProcesses = 5
        });

        async Task SaveConfigurationAsync()
        {
            var host = (await store.Get<Host>("worker-1"))!;
            host.MaxDockerContainers = 0;
            host.MaxTartVMs = 2;
            host.MaxNativeProcesses = 0;
            host.GroupId = "ui-macs";
            host.Labels["rack"] = "new";
            await store.Update(host);
        }

        var racingStore = Substitute.For<IDocumentStore>();
        var query = Substitute.For<IDocumentQuery<Host>>();
        racingStore.Query<Host>().Returns(query);
        query.ToList().Returns(async _ =>
        {
            var snapshot = await store.Query<Host>().ToList();
            await SaveConfigurationAsync();
            return snapshot;
        });
        racingStore.Get<Host>("worker-1").Returns(async _ =>
        {
            var snapshot = await store.Get<Host>("worker-1");
            await SaveConfigurationAsync();
            return snapshot;
        });
        racingStore.Update(Arg.Any<Host>()).Returns(call => store.Update(call.Arg<Host>()));
        racingStore.RunInTransaction(Arg.Any<Func<IDocumentStore, Task>>())
            .Returns(call => store.RunInTransaction(call.Arg<Func<IDocumentStore, Task>>()));

        var grain = Substitute.For<IHostGrain>();
        var processor = CreateProcessor(racingStore, hostGrain: grain);
        if (heartbeatFallback)
        {
            grain.RecordHeartbeat(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<HostResourceUsage?>())
                .Returns(Task.FromException(new InvalidOperationException("silo unavailable")));
            await processor.HandleMessageAsync("worker-1", CreateHeartbeatMessage(2), CancellationToken.None);
        }
        else
        {
            await processor.WorkerConnectedAsync(
                new AgentInfo
                {
                    AgentId = "worker-1",
                    Name = "mac-mini",
                    Platform = HostPlatform.MacOS,
                    Capabilities = ["native", "tart"]
                },
                "connection-1",
                new Dictionary<string, string> { ["tart"] = "true" },
                null,
                CancellationToken.None);
        }

        var saved = (await store.Get<Host>("worker-1"))!;
        Assert.Equal((0, 2, 0), (saved.MaxDockerContainers, saved.MaxTartVMs, saved.MaxNativeProcesses));
        Assert.Equal("ui-macs", saved.GroupId);
        Assert.Equal("new", saved.Labels["rack"]);
        Assert.NotNull(saved.LastHeartbeat);
    }

    private static HostWorkerMessage CreateHeartbeatMessage(int runningTartVms) =>
        HostWorkerProtocol.CreateMessage(
            "worker-host",
            HostWorkerMessageKinds.Heartbeat,
            new HeartbeatEvent
            {
                AgentId = "worker-host",
                RunningInstanceCount = 0,
                ResourceUsage = new HostResourceUsage { RunningTartVmCount = runningTartVms }
            });

    private static HostWorkerEventProcessor CreateProcessor(HostWorkerLogCache cache)
        => new(
            Substitute.For<IDocumentStore>(),
            Substitute.For<IGrainFactory>(),
            cache,
            new LongRunningTaskService(NullLogger<LongRunningTaskService>.Instance),
            new ConfigurationBuilder().Build(),
            NullLogger<HostWorkerEventProcessor>.Instance);

    [Fact]
    public async Task WorkerConnectedAsync_StoresEnrollmentIdentityAndSendsLabelsToGrain()
    {
        var token = HostEnrollmentToken.Create();
        var store = TestDocumentStore.Create();
        await store.Insert(new Host
        {
            Name = "pending-host",
            EnrollmentTokenHash = HostEnrollmentToken.Hash(token),
            EnrollmentTokenCreatedAt = DateTime.UtcNow
        });
        var grain = Substitute.For<IHostGrain>();
        var processor = CreateProcessor(store, hostGrain: grain);

        var hostId = await processor.WorkerConnectedAsync(
            new AgentInfo
            {
                AgentId = "worker-1",
                Name = "mac-mini-1",
                Platform = HostPlatform.MacOS,
                Architecture = "arm64"
            },
            "peer",
            new Dictionary<string, string> { ["pool"] = "mac" },
            token,
            CancellationToken.None);

        var host = await store.Get<Host>(hostId);
        Assert.NotNull(host);
        Assert.Equal("worker-1", host.WorkerId);
        Assert.Equal("mac-mini-1", host.Name);
        Assert.Null(host.EnrollmentToken);
        await grain.Received(1).Register("mac-mini-1", HostPlatform.MacOS, "arm64", "",
            Arg.Is<Dictionary<string, string>>(labels => labels["pool"] == "mac"));
    }

    [Fact]
    public async Task WorkerConnectedAsync_StoresContainerRuntimeInfo()
    {
        var token = HostEnrollmentToken.Create();
        var store = TestDocumentStore.Create();
        await store.Insert(new Host
        {
            Name = "pending-host",
            EnrollmentTokenHash = HostEnrollmentToken.Hash(token),
            EnrollmentTokenCreatedAt = DateTime.UtcNow
        });
        var processor = CreateProcessor(store);

        var hostId = await processor.WorkerConnectedAsync(
            new AgentInfo
            {
                AgentId = "worker-1",
                Name = "linux-docker",
                Platform = HostPlatform.Linux,
                Architecture = "x64",
                Runtime = new HostWorkerRuntimeInfo
                {
                    IsContainer = true,
                    ContainerId = "abcdef123456",
                    ContainerImage = "ghcr.io/redth/runnerrunner-hostworker:main"
                }
            },
            "peer",
            new Dictionary<string, string>(),
            token,
            CancellationToken.None);

        var host = await store.Get<Host>(hostId);

        Assert.NotNull(host);
        Assert.True(host.IsContainerized);
        Assert.Equal("abcdef123456", host.ContainerId);
        Assert.Equal("ghcr.io/redth/runnerrunner-hostworker:main", host.ContainerImage);
    }

    [Fact]
    public async Task WorkerConnectedAsync_MarksRestartingUpdateCurrentWhenCommitMatches()
    {
        const string commitSha = "3bf419d5a5a9f21f24f69dfb44b13639a4137448";
        var token = HostEnrollmentToken.Create();
        var store = TestDocumentStore.Create();
        var pendingHost = new Host
        {
            Id = "host-1",
            Name = "pending-host",
            EnrollmentTokenHash = HostEnrollmentToken.Hash(token),
            EnrollmentTokenCreatedAt = DateTime.UtcNow,
            UpdateStatus = "Restarting",
            LatestAvailableVersion = "v0.3.0",
            LatestAvailableCommitSha = commitSha,
            IsDraining = true,
            PendingHostWorkerUpdateSource = "release",
            PendingHostWorkerUpdateQueuedAt = DateTime.UtcNow,
            PendingHostWorkerUpdateDispatchedAt = DateTime.UtcNow
        };
        await store.Insert(pendingHost);
        using var tasks = new LongRunningTaskService(NullLogger<LongRunningTaskService>.Instance);
        tasks.TrackHostWorkerUpdate(pendingHost, "v0.3.0", commitSha, "Restarting HostWorker.", "Restarting");
        var processor = CreateProcessor(store, tasks);

        var hostId = await processor.WorkerConnectedAsync(
            new AgentInfo
            {
                AgentId = "worker-1",
                Name = "linux-ci-01",
                Platform = HostPlatform.Linux,
                Architecture = "x64",
                AgentVersion = $"v0.3.0+{commitSha}",
                AgentCommitSha = commitSha
            },
            "peer",
            new Dictionary<string, string>(),
            token,
            CancellationToken.None);

        var host = await store.Get<Host>(hostId);

        Assert.NotNull(host);
        Assert.Equal("Current", host.UpdateStatus);
        Assert.Equal(commitSha, host.AgentCommitSha);
        Assert.NotNull(host.LastUpdateCompletedAt);
        Assert.False(host.IsDraining);
        Assert.Null(host.PendingHostWorkerUpdateSource);
        var task = Assert.Single(tasks.GetSnapshot());
        Assert.Equal(LongRunningTaskStatus.Succeeded, task.Status);
        Assert.Equal(100, task.ProgressPercent);
    }

    [Fact]
    public async Task HandleMessageAsync_TracksHostWorkerUpdateStatus()
    {
        var store = TestDocumentStore.Create();
        await store.Insert(new Host
        {
            Id = "host-1",
            Name = "linux-host",
            Platform = HostPlatform.Linux,
            UpdateStatus = "Queued",
            LatestAvailableVersion = "v2.0.0"
        });
        using var tasks = new LongRunningTaskService(NullLogger<LongRunningTaskService>.Instance);
        var processor = CreateProcessor(store, tasks);
        var message = HostWorkerProtocol.CreateMessage(
            "worker-host",
            HostWorkerMessageKinds.UpdateStatus,
            new HostWorkerUpdateStatusEvent
            {
                HostId = "worker-host",
                TargetVersion = "v2.0.0",
                Stage = "downloading",
                Message = "Downloading update.",
                Success = true
            });

        await processor.HandleMessageAsync("host-1", message, CancellationToken.None);

        var host = await store.Get<Host>("host-1");
        Assert.NotNull(host);
        Assert.Equal("Downloading", host.UpdateStatus);
        var task = Assert.Single(tasks.GetSnapshot());
        Assert.Equal(LongRunningTaskKind.HostWorkerUpdate, task.Kind);
        Assert.Equal(LongRunningTaskStatus.Running, task.Status);
        Assert.Equal("Downloading update.", task.StatusText);
        Assert.Equal(30, task.ProgressPercent);
    }

    [Fact]
    public async Task WorkerConnectedAsync_RejectsWrongTokenForKnownHost()
    {
        var token = HostEnrollmentToken.Create();
        var store = TestDocumentStore.Create();
        await store.Insert(new Host
        {
            Id = "host-1",
            Name = "mac-mini-1",
            WorkerId = "worker-1",
            EnrollmentTokenHash = HostEnrollmentToken.Hash(token),
            IsApproved = true
        });
        var processor = CreateProcessor(store);

        await Assert.ThrowsAsync<Grpc.Core.RpcException>(() => processor.WorkerConnectedAsync(
            new AgentInfo
            {
                AgentId = "worker-1",
                Name = "mac-mini-1",
                Platform = HostPlatform.MacOS
            },
            "peer",
            new Dictionary<string, string>(),
            "wrong-token",
            CancellationToken.None));
    }

    private static HostWorkerEventProcessor CreateProcessor(IDocumentStore store, LongRunningTaskService? tasks = null, IHostGrain? hostGrain = null)
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IHostGrain>(Arg.Any<string>(), null).Returns(hostGrain ?? Substitute.For<IHostGrain>());
        grainFactory.GetGrain<ISchedulerGrain>(Arg.Any<long>(), null).Returns(Substitute.For<ISchedulerGrain>());

        return new HostWorkerEventProcessor(
            store,
            grainFactory,
            CreateCache(),
            tasks ?? new LongRunningTaskService(NullLogger<LongRunningTaskService>.Instance),
            new ConfigurationBuilder().Build(),
            NullLogger<HostWorkerEventProcessor>.Instance);
    }

    private static HostWorkerLogCache CreateCache()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HostWorker:LogCache:MaxFramesPerStream"] = "10"
            })
            .Build();

        return new HostWorkerLogCache(configuration);
    }
}
