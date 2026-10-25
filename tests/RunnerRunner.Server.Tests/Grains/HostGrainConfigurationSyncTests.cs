using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains.Interfaces;
using RunnerRunner.Server.Hubs;
using RunnerRunner.Server.Services;
using RunnerRunner.Server.Services.HostWorkers;
using RunnerRunner.Server.Tests.TestSupport;
using Shiny.DocumentDb;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Tests.Grains;

[Collection(OrleansClusterCollection.Name)]
public sealed class HostGrainConfigurationSyncTests
{
    private readonly IGrainFactory _grainFactory;
    private readonly IDocumentStore _store;

    public HostGrainConfigurationSyncTests(OrleansTestClusterFixture fixture)
    {
        _grainFactory = fixture.GrainFactory;
        _store = fixture.DocumentStore;
    }

    [Fact]
    public async Task Activation_AdoptsPersistedDocumentConfigurationBeforeRegistrationOrHeartbeat()
    {
        var hostId = OrleansTestIds.Create("persisted-config");
        // Production Orleans JSON omits zero fields, so grain initializers can
        // be 10/0/5 even when the document correctly stores disabled backends.
        // Activation must adopt the document before the scheduler sees state.
        await _store.Insert(new Host
        {
            Id = hostId, Name = $"{hostId}-name",
            MaxDockerContainers = 0, MaxTartVMs = 2, MaxNativeProcesses = 0,
            BackendLimitsConfigured = true, GroupId = "mac-farm",
            Labels = new() { ["rack"] = "a" }, IsApproved = true
        });
        var grain = _grainFactory.GetGrain<IHostGrain>(hostId);
        var state = await grain.GetState();
        Assert.Equal((0, 2, 0, "mac-farm"),
            (state.MaxDockerContainers, state.MaxTartVMs, state.MaxNativeProcesses, state.GroupId));
        Assert.True(state.BackendLimitsConfigured);
        Assert.Equal("a", state.Labels["rack"]);
        Assert.False(await grain.CanAcceptRunner(ExecutionBackend.Native));
        Assert.False(await grain.CanAcceptRunner(ExecutionBackend.Docker));
    }

    [Fact]
    public async Task Heartbeat_DoesNotRevertBackendLimitsOrGroupSavedOnHostDocument()
    {
        var hostId = OrleansTestIds.Create("mac-host");
        // HostWorkerEventProcessor inserts the document before registering the grain.
        await _store.Insert(new Host
        {
            Id = hostId,
            Name = $"{hostId}-name",
            Platform = HostPlatform.MacOS,
            Capabilities = ["native", "tart"],
            IsApproved = true
        });

        var grain = _grainFactory.GetGrain<IHostGrain>(hostId);
        await grain.Register($"{hostId}-name", HostPlatform.MacOS, "arm64", "1.0.0");
        await grain.UpdateLabels(new Dictionary<string, string> { ["native"] = "true", ["tart"] = "true" });

        // Values differ from both the grain defaults and the new-host defaults.
        var document = await _store.Get<Host>(hostId);
        Assert.NotNull(document);
        document.MaxDockerContainers = 0;
        document.MaxTartVMs = 2;
        document.MaxNativeProcesses = 0;
        document.GroupId = "mac-farm";
        await _store.Update(document);

        await grain.RecordHeartbeat("connection-1", 0, null);

        var reloaded = await _store.Get<Host>(hostId);
        Assert.NotNull(reloaded);
        Assert.Equal(0, reloaded.MaxDockerContainers);
        Assert.Equal(2, reloaded.MaxTartVMs);
        Assert.Equal(0, reloaded.MaxNativeProcesses);
        Assert.Equal("mac-farm", reloaded.GroupId);

        Assert.False(await grain.CanAcceptRunner(ExecutionBackend.Native));
        Assert.False(await grain.CanAcceptRunner(ExecutionBackend.Docker));
        Assert.True(await grain.CanAcceptRunner(ExecutionBackend.Tart));
    }

    [Theory]
    [InlineData(HostPlatform.MacOS, "tart")]
    [InlineData(HostPlatform.Linux, "docker")]
    public async Task Heartbeat_PreservesExistingConfigurationWithoutMigration(HostPlatform platform, string backend)
    {
        var hostId = OrleansTestIds.Create("configured-host");
        await InsertHostAsync(hostId, platform, ["native", backend], docker: 4, tart: 2, native: 3);
        var grain = await RegisterAsync(hostId, platform, backend);
        await grain.RecordHeartbeat("connection-1", 0, null);

        var reloaded = await _store.Get<Host>(hostId);
        Assert.Equal((4, 2, 3), (reloaded!.MaxDockerContainers, reloaded.MaxTartVMs, reloaded.MaxNativeProcesses));
        Assert.True(await grain.CanAcceptRunner(ExecutionBackend.Native));
    }

    [Fact]
    public async Task NewCapability_DoesNotEnableBackendOrChangeExistingLimits()
    {
        var hostId = OrleansTestIds.Create("native-host");
        await InsertHostAsync(hostId, HostPlatform.MacOS, ["native"], docker: 0, tart: 0, native: 2);
        var grain = await RegisterAsync(hostId, HostPlatform.MacOS);
        await grain.UpdateLabels(new Dictionary<string, string> { ["tart"] = "true" });
        await grain.RecordHeartbeat("connection-1", 0, null);

        var host = (await _store.Get<Host>(hostId))!;
        Assert.Equal((0, 0, 2), (host.MaxDockerContainers, host.MaxTartVMs, host.MaxNativeProcesses));
        Assert.False(await grain.CanAcceptRunner(ExecutionBackend.Tart));
    }

    [Fact]
    public async Task Heartbeat_DoesNotReplaceAdvertisedCapabilitiesWithUiLabels()
    {
        var hostId = OrleansTestIds.Create("mac-host");
        await InsertHostAsync(hostId, HostPlatform.MacOS, ["native", "tart"], docker: 0, tart: 3, native: 5);
        var grain = await RegisterAsync(hostId, HostPlatform.MacOS, "tart");
        await grain.SetConfiguration(0, 2, 0, null, new Dictionary<string, string> { ["pool"] = "mac" });
        await grain.RecordHeartbeat("connection-1", 0, null);

        var host = (await _store.Get<Host>(hostId))!;
        Assert.Equal(["native", "tart"], host.Capabilities);
        Assert.Equal(new Dictionary<string, string> { ["pool"] = "mac" }, host.Labels);
    }

    [Fact]
    public async Task SetConfiguration_UpdatesDocumentAndSchedulerBeforeNextHeartbeat()
    {
        var hostId = OrleansTestIds.Create("mac-host");
        await InsertHostAsync(hostId, HostPlatform.MacOS, ["native", "tart"], docker: 0, tart: 3, native: 5);
        var grain = await RegisterAsync(hostId, HostPlatform.MacOS, "tart");
        await grain.UpdateLabels(new Dictionary<string, string> { ["removed"] = "old" });

        var labels = new Dictionary<string, string>((await _store.Get<Host>(hostId))!.Labels) { ["rack"] = "a" };
        labels.Remove("removed");
        await grain.SetConfiguration(maxDocker: 0, maxTart: 2, maxNative: 0, groupId: "mac-farm", labels);

        var reloaded = await _store.Get<Host>(hostId);
        Assert.Equal("a", reloaded!.Labels["rack"]);
        Assert.DoesNotContain("removed", reloaded.Labels.Keys);
        Assert.Equal((0, 2, 0, "mac-farm"), (reloaded.MaxDockerContainers, reloaded.MaxTartVMs, reloaded.MaxNativeProcesses, reloaded.GroupId));
        Assert.False(await grain.CanAcceptRunner(ExecutionBackend.Native));
        Assert.True(await grain.CanAcceptRunner(ExecutionBackend.Tart));

        await grain.RecordHeartbeat("connection-1", 0, null);
        reloaded = await _store.Get<Host>(hostId);
        Assert.Equal((0, 2, 0, "mac-farm"), (reloaded!.MaxDockerContainers, reloaded.MaxTartVMs, reloaded.MaxNativeProcesses, reloaded.GroupId));
        Assert.Equal("a", reloaded.Labels["rack"]);
        Assert.DoesNotContain("removed", reloaded.Labels.Keys);
    }

    [Fact]
    public async Task SetConfiguration_UnregisteredHostPreservesIdentityAndRuntimeFields()
    {
        var hostId = OrleansTestIds.Create("offline-host");
        await InsertHostAsync(hostId, HostPlatform.MacOS, ["native", "tart"], docker: 10, tart: 0, native: 5);
        var grain = _grainFactory.GetGrain<IHostGrain>(hostId);
        await grain.SetConfiguration(0, 2, 1, "offline-pool", new Dictionary<string, string> { ["rack"] = "b" });

        var host = (await _store.Get<Host>(hostId))!;
        Assert.Equal($"{hostId}-name", host.Name);
        Assert.Equal(HostPlatform.MacOS, host.Platform);
        Assert.Equal(AgentStatus.Offline, host.AgentStatus);
        Assert.Null(host.LastHeartbeat);
        Assert.Equal(["native", "tart"], host.Capabilities);
        Assert.Equal((0, 2, 1, "offline-pool"), (host.MaxDockerContainers, host.MaxTartVMs, host.MaxNativeProcesses, host.GroupId));
    }

    [Fact]
    public async Task SetConfiguration_MissingHostReportsFailure()
    {
        var grain = _grainFactory.GetGrain<IHostGrain>(OrleansTestIds.Create("missing-host"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => grain.SetConfiguration(0, 2, 0, null));
    }

    [Fact]
    public async Task ExplicitLimitsAndGroupBeforeRegistrationAreNotReplacedByDefaults()
    {
        var hostId = OrleansTestIds.Create("preconfigured-host");
        var grain = _grainFactory.GetGrain<IHostGrain>(hostId);
        await grain.SetResourceLimits(0, 1, 2);
        await grain.SetGroupId("preconfigured");
        await grain.Register($"{hostId}-name", HostPlatform.MacOS, "arm64", "1.0.0",
            new Dictionary<string, string> { ["native"] = "true", ["tart"] = "true" });

        var host = (await _store.Get<Host>(hostId))!;
        Assert.Equal((0, 1, 2, "preconfigured"), (host.MaxDockerContainers, host.MaxTartVMs, host.MaxNativeProcesses, host.GroupId));
    }

    [Theory]
    [InlineData(-1, 2, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 2, -1)]
    public async Task SetConfiguration_InvalidLimitsLeaveSavedConfigurationUntouched(int docker, int tart, int native)
    {
        var hostId = OrleansTestIds.Create("configured-host");
        await InsertHostAsync(hostId, HostPlatform.MacOS, ["native", "tart"], docker: 0, tart: 3, native: 2);
        var grain = _grainFactory.GetGrain<IHostGrain>(hostId);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => grain.SetConfiguration(docker, tart, native, "invalid"));
        var host = (await _store.Get<Host>(hostId))!;
        Assert.Equal((0, 3, 2), (host.MaxDockerContainers, host.MaxTartVMs, host.MaxNativeProcesses));
        Assert.Null(host.GroupId);
    }

    [Theory]
    [InlineData(HostPlatform.MacOS, "tart", 0, 3, 0)]
    [InlineData(HostPlatform.Linux, "docker", 10, 0, 0)]
    [InlineData(HostPlatform.Windows, "native", 0, 0, 5)]
    public async Task Register_AppliesIsolatedDefaultsOnlyForNewHosts(HostPlatform platform, string backend, int docker, int tart, int native)
    {
        var hostId = OrleansTestIds.Create("new-host");
        var grain = _grainFactory.GetGrain<IHostGrain>(hostId);
        var labels = new Dictionary<string, string> { ["native"] = "true", [backend] = "true" };
        await grain.Register($"{hostId}-name", platform, "arm64", "1.0.0", labels);
        var host = (await _store.Get<Host>(hostId))!;
        Assert.Equal((docker, tart, native), (host.MaxDockerContainers, host.MaxTartVMs, host.MaxNativeProcesses));

        await grain.SetConfiguration(0, 1, 2, "custom", labels);
        await grain.Register($"{hostId}-name", platform, "arm64", "1.0.1", labels);
        await grain.RecordHeartbeat("connection-1", 0, null);
        host = (await _store.Get<Host>(hostId))!;
        Assert.Equal((0, 1, 2, "custom"), (host.MaxDockerContainers, host.MaxTartVMs, host.MaxNativeProcesses, host.GroupId));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FirstEnrollment_UsesVmDefaultsUnlessUiAlreadyConfiguredHost(bool configuredInUi, bool keepModelDefaults)
    {
        var hostId = OrleansTestIds.Create("pending-host");
        var token = HostEnrollmentToken.Create();
        await _store.Insert(new Host
        {
            Id = hostId,
            Name = $"{hostId}-pending",
            EnrollmentTokenHash = HostEnrollmentToken.Hash(token),
            IsApproved = false
        });
        var grain = _grainFactory.GetGrain<IHostGrain>(hostId);
        if (configuredInUi)
            await grain.SetConfiguration(
                keepModelDefaults ? 10 : 0, keepModelDefaults ? 0 : 1, keepModelDefaults ? 5 : 2,
                "custom", new Dictionary<string, string> { ["rack"] = "a" });

        var configuration = new ConfigurationBuilder().Build();
        using var tasks = new LongRunningTaskService(NullLogger<LongRunningTaskService>.Instance);
        var processor = new HostWorkerEventProcessor(
            _store, _grainFactory, new HostWorkerLogCache(configuration), tasks,
            configuration, NullLogger<HostWorkerEventProcessor>.Instance);
        var info = new AgentInfo
        {
            AgentId = $"{hostId}-worker",
            Name = $"{hostId}-name",
            Platform = HostPlatform.MacOS,
            Architecture = "arm64",
            Capabilities = ["native", "tart", "docker"]
        };
        var labels = new Dictionary<string, string> { ["pool"] = "mac" };

        Assert.Equal(hostId, await processor.WorkerConnectedAsync(info, "connection-1", labels, token, CancellationToken.None));
        var host = (await _store.Get<Host>(hostId))!;
        var expected = configuredInUi ? keepModelDefaults ? (10, 0, 5) : (0, 1, 2) : (0, 3, 0);
        Assert.Equal(expected, (host.MaxDockerContainers, host.MaxTartVMs, host.MaxNativeProcesses));
        Assert.Equal("mac", host.Labels["pool"]);
        Assert.True(host.IsApproved);
        Assert.NotNull(host.EnrolledAt);
        Assert.Equal(info.AgentId, host.WorkerId);
        if (configuredInUi)
        {
            Assert.Equal("custom", host.GroupId);
            Assert.Equal("a", host.Labels["rack"]);
        }

        await grain.SetConfiguration(0, 2, 1, "saved", host.Labels);
        await processor.WorkerConnectedAsync(info, "connection-2", labels, token, CancellationToken.None);
        host = (await _store.Get<Host>(hostId))!;
        Assert.Equal((0, 2, 1, "saved"), (host.MaxDockerContainers, host.MaxTartVMs, host.MaxNativeProcesses, host.GroupId));
        Assert.True(await grain.CanAcceptRunner(ExecutionBackend.Native));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyAgentRegistration_InitializesOnlyPendingHosts(bool alreadyApproved)
    {
        var hostId = OrleansTestIds.Create("legacy-agent");
        var host = new Host
        {
            Id = hostId,
            Name = $"{hostId}-name",
            Platform = HostPlatform.MacOS,
            IsApproved = alreadyApproved,
            MaxDockerContainers = alreadyApproved ? 4 : 10,
            MaxTartVMs = alreadyApproved ? 1 : 0,
            MaxNativeProcesses = alreadyApproved ? 2 : 5
        };
        await _store.Insert(host);
        var context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns("legacy-connection");
        using var hub = new AgentHub(NullLogger<AgentHub>.Instance, _store, _grainFactory) { Context = context };
        try
        {
            await hub.AgentConnected(new AgentInfo
            {
                AgentId = hostId,
                Name = host.Name,
                Platform = HostPlatform.MacOS,
                Architecture = "arm64",
                Capabilities = ["native", "tart"]
            });
            await hub.Heartbeat(new HeartbeatEvent { AgentId = hostId });

            var saved = (await _store.Get<Host>(hostId))!;
            var expected = alreadyApproved ? (4, 1, 2) : (0, 3, 0);
            Assert.Equal(expected, (saved.MaxDockerContainers, saved.MaxTartVMs, saved.MaxNativeProcesses));
            Assert.True(saved.IsApproved);
            Assert.NotNull(saved.EnrolledAt);
        }
        finally
        {
            await hub.AgentDisconnected(hostId);
        }
    }

    private Task InsertHostAsync(
        string hostId,
        HostPlatform platform,
        List<string> capabilities,
        int docker,
        int tart,
        int native) =>
        _store.Insert(new Host
        {
            Id = hostId,
            Name = $"{hostId}-name",
            Platform = platform,
            Capabilities = capabilities,
            MaxDockerContainers = docker,
            MaxTartVMs = tart,
            MaxNativeProcesses = native,
            IsApproved = true
        });

    private async Task<IHostGrain> RegisterAsync(string hostId, HostPlatform platform, params string[] capabilities)
    {
        // Mirrors HostWorkerEventProcessor.WorkerConnectedAsync: register, labels, heartbeat.
        var grain = _grainFactory.GetGrain<IHostGrain>(hostId);
        await grain.Register($"{hostId}-name", platform, "arm64", "1.0.0");
        var labels = new Dictionary<string, string> { ["native"] = "true" };
        foreach (var capability in capabilities)
            labels[capability] = "true";
        await grain.UpdateLabels(labels);
        await grain.RecordHeartbeat("connection-1", 0, null);
        return grain;
    }
}
