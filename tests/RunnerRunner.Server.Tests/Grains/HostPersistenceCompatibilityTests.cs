using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Serialization;
using Orleans.Serialization;
using Orleans.Storage;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains.State;
using RunnerRunner.Server.Services;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Tests.Grains;

public sealed class HostPersistenceCompatibilityTests
{
    // Shiny.DocumentDb 3.2 uses camelCase and otherwise default System.Text.Json
    // options when DocumentStoreSetup does not override them.
    private static readonly JsonSerializerOptions DocumentJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void HostDocument_MainReaderAcceptsLiveFieldButLosesItOnRewrite()
    {
        var live = new Host
        {
            Id = "compat-host", Name = "compat-host", Platform = HostPlatform.MacOS,
            MaxDockerContainers = 0, MaxTartVMs = 2, MaxNativeProcesses = 0,
            BackendLimitsConfigured = true, GroupId = "mac-farm",
            Labels = new() { ["rack"] = "a" }, Capabilities = ["native", "tart"],
            IsApproved = true
        };
        var json = JsonSerializer.Serialize(live, DocumentJson);
        var main = JsonSerializer.Deserialize<MainHost>(json, DocumentJson)!;
        Assert.Equal((0, 2, 0, "mac-farm"),
            (main.MaxDockerContainers, main.MaxTartVMs, main.MaxNativeProcesses, main.GroupId));
        Assert.Equal("a", main.Labels["rack"]);
        Assert.Equal(live.Capabilities, main.Capabilities);

        // An old full-document writer does not retain unknown fields. This is a
        // behavioral rollback risk, not an inability to deserialize the record.
        var rewritten = JsonSerializer.Serialize(main, DocumentJson);
        Assert.DoesNotContain("backendLimitsConfigured", rewritten);
        var restored = JsonSerializer.Deserialize<Host>(rewritten, DocumentJson)!;
        Assert.False(restored.BackendLimitsConfigured);
        Assert.Equal((0, 2, 0), (restored.MaxDockerContainers, restored.MaxTartVMs, restored.MaxNativeProcesses));
    }

    [Fact]
    public void HostDocument_OldPayloadRetainsInitializersAndExplicitZeroLimits()
    {
        // Representative old serialized records, not a dump of live host data.
        const string missing = """{"id":"compat-host","name":"compat-host"}""";
        var host = JsonSerializer.Deserialize<Host>(missing, DocumentJson)!;
        Assert.False(host.BackendLimitsConfigured);
        Assert.Equal((10, 0, 5), (host.MaxDockerContainers, host.MaxTartVMs, host.MaxNativeProcesses));
        var old = new MainHost
        {
            Id = "compat-host", Name = "compat-host",
            MaxDockerContainers = 0, MaxTartVMs = 1, MaxNativeProcesses = 0
        };
        host = JsonSerializer.Deserialize<Host>(JsonSerializer.Serialize(old, DocumentJson), DocumentJson)!;
        Assert.Equal((0, 1, 0), (host.MaxDockerContainers, host.MaxTartVMs, host.MaxNativeProcesses));
        Assert.False(host.BackendLimitsConfigured);
    }

    [Fact]
    public async Task DocumentStore_PartialRuntimeWritePreservesLiveMarkerAndLimits()
    {
        var store = TestDocumentStore.Create();
        var host = new Host
        {
            Id = "compat-host", Name = "compat-host", BackendLimitsConfigured = true,
            MaxDockerContainers = 0, MaxTartVMs = 2, MaxNativeProcesses = 0
        };
        await store.Insert(host);
        var stale = new Host { Id = host.Id, Name = "runtime", BackendLimitsConfigured = false };
        await HostDocumentUpdates.UpdateAsync(store, stale, h => h.Name);
        var saved = (await store.Get<Host>(host.Id))!;
        Assert.True(saved.BackendLimitsConfigured);
        Assert.Equal((0, 2, 0), (saved.MaxDockerContainers, saved.MaxTartVMs, saved.MaxNativeProcesses));
    }

    [Fact]
    public void GrainState_OrleansBinaryOldReaderSkipsId24AndRewriteLosesMarker()
    {
        using var services = CreateSerializerServices();
        var storage = new OrleansGrainStorageSerializer(services.GetRequiredService<Serializer>());
        var live = new HostGrainState
        {
            Name = "compat-host", Platform = HostPlatform.MacOS,
            MaxDockerContainers = 0, MaxTartVMs = 2, MaxNativeProcesses = 0,
            BackendLimitsConfigured = true, GroupId = "mac-farm",
            Labels = new() { ["rack"] = "a" }, ObservedFreeDiskBytes = 1234567890123,
            ObservedDiskUsageAt = new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc)
        };
        var bytes = storage.Serialize(live);
        var candidate = storage.Deserialize<HostGrainState>(bytes);
        Assert.True(candidate.BackendLimitsConfigured);

        // Both calls use a concrete expected type, as the HostGrain persistent
        // state does. The frozen reader has the original IDs 0..23 and no ID 24.
        var main = storage.Deserialize<MainHostGrainState>(bytes);
        Assert.Equal((0, 2, 0, "mac-farm"),
            (main.MaxDockerContainers, main.MaxTartVMs, main.MaxNativeProcesses, main.GroupId));
        Assert.Equal("a", main.Labels["rack"]);
        Assert.Equal(live.ObservedFreeDiskBytes, main.ObservedFreeDiskBytes);
        Assert.Equal(live.ObservedDiskUsageAt, main.ObservedDiskUsageAt);
        var restored = storage.Deserialize<HostGrainState>(storage.Serialize(main));
        Assert.False(restored.BackendLimitsConfigured);
        Assert.Equal(2, restored.MaxTartVMs);
    }

    [Fact]
    public void GrainState_OldBinaryPayloadGetsFalseMarkerWithoutChangingLimits()
    {
        using var services = CreateSerializerServices();
        var storage = new OrleansGrainStorageSerializer(services.GetRequiredService<Serializer>());
        var old = new MainHostGrainState
        {
            Name = "compat-host", MaxDockerContainers = 0, MaxTartVMs = 1, MaxNativeProcesses = 0
        };
        var restored = storage.Deserialize<HostGrainState>(storage.Serialize(old));
        Assert.False(restored.BackendLimitsConfigured);
        Assert.Equal((0, 1, 0), (restored.MaxDockerContainers, restored.MaxTartVMs, restored.MaxNativeProcesses));
    }

    [Fact]
    public void GrainState_ProductionJsonSkipsUnknownMarkerButOmittedZerosUseInitializers()
    {
        var candidateStorage = CreateJsonStorage(legacyReader: false);
        var mainStorage = CreateJsonStorage(legacyReader: true);
        var live = new HostGrainState
        {
            Name = "compat-host", MaxDockerContainers = 0, MaxTartVMs = 2, MaxNativeProcesses = 0,
            BackendLimitsConfigured = true, GroupId = "mac-farm",
            Labels = new() { ["rack"] = "a" }
        };
        var payload = candidateStorage.Serialize(live);
        Assert.Contains("\"BackendLimitsConfigured\":true", payload.ToString());
        Assert.DoesNotContain("\"MaxDockerContainers\"", payload.ToString());
        Assert.DoesNotContain("\"MaxNativeProcesses\"", payload.ToString());
        var main = mainStorage.Deserialize<MainHostGrainState>(payload);
        Assert.Equal((10, 2, 5, "mac-farm"),
            (main.MaxDockerContainers, main.MaxTartVMs, main.MaxNativeProcesses, main.GroupId));
        Assert.Equal("a", main.Labels["rack"]);
        var restored = candidateStorage.Deserialize<HostGrainState>(mainStorage.Serialize(main));
        Assert.False(restored.BackendLimitsConfigured);
        Assert.Equal((10, 2, 5), (restored.MaxDockerContainers, restored.MaxTartVMs, restored.MaxNativeProcesses));
    }

    [Fact]
    public void GrainState_ProductionJsonOldPayloadDefaultsMarkerFalse()
    {
        var mainStorage = CreateJsonStorage(legacyReader: true);
        var candidateStorage = CreateJsonStorage(legacyReader: false);
        var old = new MainHostGrainState { Name = "compat-host", MaxTartVMs = 1 };
        var restored = candidateStorage.Deserialize<HostGrainState>(mainStorage.Serialize(old));
        Assert.False(restored.BackendLimitsConfigured);
        Assert.Equal((10, 1, 5), (restored.MaxDockerContainers, restored.MaxTartVMs, restored.MaxNativeProcesses));
    }

    private static JsonGrainStorageSerializer CreateJsonStorage(bool legacyReader)
    {
        var options = new OrleansJsonSerializerOptions();
        // Production's two builds use the SAME fully qualified state type name.
        // Map only that name to the frozen old contract to exercise an old reader
        // in the same process. All other JSON defaults remain Orleans 10.0.1's.
        options.JsonSerializerSettings.SerializationBinder = new StateContractBinder(legacyReader);
        return new JsonGrainStorageSerializer(new OrleansJsonSerializer(Options.Create(options)));
    }

    private sealed class StateContractBinder(bool legacyReader) : ISerializationBinder
    {
        private readonly DefaultSerializationBinder _fallback = new();

        public Type BindToType(string? assemblyName, string typeName) =>
            typeName == typeof(HostGrainState).FullName
                ? legacyReader ? typeof(MainHostGrainState) : typeof(HostGrainState)
                : _fallback.BindToType(assemblyName, typeName);

        public void BindToName(Type serializedType, out string? assemblyName, out string? typeName) =>
            _fallback.BindToName(serializedType == typeof(MainHostGrainState) ? typeof(HostGrainState) : serializedType,
                out assemblyName, out typeName);
    }

    private static ServiceProvider CreateSerializerServices() =>
        new ServiceCollection().AddSerializer(builder => builder
            .AddAssembly(typeof(HostGrainState).Assembly)
            .AddAssembly(typeof(MainHostGrainState).Assembly)).BuildServiceProvider();
}

// Frozen property contracts from main 761a9c2 (also e13cfbc^), with comments
// omitted and test-only names. Do not derive from candidate models: that would
// accidentally make the "old" reader aware of the new field.
public class MainHost
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public required string Name { get; set; }
    public string? DisplayName { get; set; }
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
    public HostPlatform Platform { get; set; }
    public AgentStatus AgentStatus { get; set; } = AgentStatus.Offline;
    public DateTime? LastHeartbeat { get; set; }
    public string? AgentVersion { get; set; }
    public string? AgentCommitSha { get; set; }
    public string? AgentBuildTag { get; set; }
    public string? OsVersion { get; set; }
    public string? Architecture { get; set; }
    public bool IsContainerized { get; set; }
    public string? ContainerId { get; set; }
    public string? ContainerImage { get; set; }
    public List<string> Capabilities { get; set; } = [];
    public Dictionary<string, string> EnvironmentOverrides { get; set; } = new();
    public string? EnrollmentToken { get; set; }
    public string? EnrollmentTokenHash { get; set; }
    public DateTime? EnrollmentTokenCreatedAt { get; set; }
    public DateTime? EnrolledAt { get; set; }
    public string? WorkerId { get; set; }
    public string? LatestAvailableVersion { get; set; }
    public string? LatestAvailableCommitSha { get; set; }
    public DateTime? LastUpdateCheckAt { get; set; }
    public string? UpdateStatus { get; set; }
    public string? UpdateMessage { get; set; }
    public DateTime? LastUpdateStartedAt { get; set; }
    public DateTime? LastUpdateCompletedAt { get; set; }
    public bool IsDraining { get; set; }
    public string? PendingHostWorkerUpdateSource { get; set; }
    public string? PendingHostWorkerUpdateVersion { get; set; }
    public bool PendingHostWorkerUpdateAllowNonUpgrade { get; set; }
    public string? PendingHostWorkerUpdatePublicBaseUrl { get; set; }
    public DateTime? PendingHostWorkerUpdateQueuedAt { get; set; }
    public DateTime? PendingHostWorkerUpdateDispatchedAt { get; set; }
    public bool IsApproved { get; set; }
    public string? RunnerBasePath { get; set; }
    public string? WorkDirectory { get; set; }
    public Dictionary<string, string> ReportedEnvironment { get; set; } = new();
    public Dictionary<string, string> Labels { get; set; } = new();
    public int MaxDockerContainers { get; set; } = 10;
    public int MaxTartVMs { get; set; }
    public int MaxNativeProcesses { get; set; } = 5;
    public int? ObservedRunningTartVMs { get; set; }
    public long? ObservedFreeDiskBytes { get; set; }
    public long? ObservedTotalDiskBytes { get; set; }
    public DateTime? ObservedResourceUsageAt { get; set; }
    public DateTime? ObservedDiskUsageAt { get; set; }
    public string? GroupId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

[GenerateSerializer]
public class MainHostGrainState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public string? DisplayName { get; set; }
    [Id(2)] public HostPlatform Platform { get; set; }
    [Id(3)] public string? Architecture { get; set; }
    [Id(4)] public string? AgentVersion { get; set; }
    [Id(5)] public Dictionary<string, string> Labels { get; set; } = new();
    [Id(6)] public string? GroupId { get; set; }
    [Id(7)] public int MaxDockerContainers { get; set; } = 10;
    [Id(8)] public int MaxTartVMs { get; set; }
    [Id(9)] public int MaxNativeProcesses { get; set; } = 5;
    [Id(10)] public int RunningDockerContainers { get; set; }
    [Id(11)] public int RunningTartVMs { get; set; }
    [Id(12)] public int RunningNativeProcesses { get; set; }
    [Id(13)] public AgentStatus Status { get; set; } = AgentStatus.Offline;
    [Id(14)] public string? ConnectionId { get; set; }
    [Id(15)] public DateTime? LastHeartbeat { get; set; }
    [Id(16)] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [Id(17)] public Dictionary<string, string> ReportedEnvironment { get; set; } = new();
    [Id(18)] public int? ObservedRunningTartVMs { get; set; }
    [Id(19)] public DateTime? ObservedResourceUsageAt { get; set; }
    [Id(20)] public bool IsDraining { get; set; }
    [Id(21)] public long? ObservedFreeDiskBytes { get; set; }
    [Id(22)] public long? ObservedTotalDiskBytes { get; set; }
    [Id(23)] public DateTime? ObservedDiskUsageAt { get; set; }
}
