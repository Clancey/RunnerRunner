using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Serialization;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Hosting;
using Orleans.Serialization;
using Orleans.Storage;
using Orleans.TestingHost;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Data;
using RunnerRunner.Server.Grains.Interfaces;
using RunnerRunner.Server.Grains.State;
using RunnerRunner.Server.Tests.TestSupport;
using Shiny.DocumentDb;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Tests.Grains;

public sealed class HostConfigurationRollbackTests
{
    [Fact]
    public async Task FiveHostConfigurations_FirstActivationAndRollbackNeverProjectRehydratedDockerDefault()
    {
        using var fixtures = JsonDocument.Parse(HostConfigurationRollbackFixtures.Json);
        var documentOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        var hostTypeInfo = Assert.IsAssignableFrom<JsonTypeInfo<Host>>(documentOptions.GetTypeInfo(typeof(Host)));
        // Match the original document marker absence when seeding SQLite too:
        // ordinary model serialization would materialize the missing bool as false.
        // Do not omit zero limits, which the documents explicitly contain.
        hostTypeInfo.Properties.Single(property => property.Name == "backendLimitsConfigured")
            .ShouldSerialize = (_, value) => value is true;
        var cases = fixtures.RootElement.GetProperty("hosts").EnumerateObject()
            .Select(entry => new HostCase(
                entry.Name,
                entry.Value.GetProperty("document").Deserialize<Host>(documentOptions)!,
                new BinaryData(entry.Value.GetProperty("grain_json").GetRawText())))
            .ToArray();
        Assert.Equal(5, cases.Length);
        var store = TestDocumentStore.Create();
        using (var services = new ServiceCollection().AddSingleton(store).BuildServiceProvider())
            await DatabaseInitializer.EnsureTablesCreatedAsync(services);
        var storage = new RecordingJsonHostStorage();
        var ids = new Dictionary<string, string>();
        foreach (var host in cases)
        {
            var id = host.Document.Id;
            ids.Add(host.Name, id);
            Assert.True(host.Document.IsApproved);
            Assert.NotNull(host.Document.EnrolledAt);
            await store.Insert(host.Document, hostTypeInfo);
            // Seed the selected actual persisted JSON, not a freshly serialized
            // model: absence, $type/$id and dictionary metadata are significant.
            storage.Payloads[id] = host.Payload;
            Assert.Equal(host.Docker != 0, storage.Payloads[id].ToString().Contains("\"MaxDockerContainers\""));
            Assert.Equal(host.Configured, storage.Payloads[id].ToString().Contains("\"BackendLimitsConfigured\""));
            var originalDocument = fixtures.RootElement.GetProperty("hosts").GetProperty(host.Name).GetProperty("document");
            Assert.Equal(host.Configured, originalDocument.TryGetProperty("backendLimitsConfigured", out _));
        }

        SiloConfigurator.DocumentStore = store;
        SiloConfigurator.Storage = storage;
        try
        {
            using (var candidate = CreateCluster())
            {
                candidate.Deploy();
                foreach (var host in cases)
                    await AssertActivationAndWritesAsync(candidate, store, storage, ids[host.Name], host);
                candidate.StopAllSilos();
            }

            // Freeze the deployed 3d2ee43/e13 JSON property contract, rather than
            // deserialize into a model that could silently acquire future fields.
            var options = new OrleansJsonSerializerOptions();
            options.JsonSerializerSettings.SerializationBinder = new DeployedStateBinder();
            var deployedReader = new JsonGrainStorageSerializer(new OrleansJsonSerializer(Options.Create(options)));
            foreach (var host in cases)
            {
                var payload = storage.Payloads[ids[host.Name]];
                var old = deployedReader.Deserialize<DeployedHostGrainState>(payload);
                Assert.Equal((10, host.Tart, host.Native, host.Configured),
                    (old.MaxDockerContainers, old.MaxTartVMs, old.MaxNativeProcesses, old.BackendLimitsConfigured));
                Assert.Equal(host.Document.GroupId, old.GroupId);
                Assert.Equal(host.Document.Labels, old.Labels);
                Assert.Equal(host.Docker != 0, payload.ToString().Contains("\"MaxDockerContainers\""));
                Assert.Equal(host.Configured, payload.ToString().Contains("\"BackendLimitsConfigured\""));
            }

            // A separate silo forces a real persistent-state read and fresh
            // OnActivateAsync, not another call on a cached candidate activation.
            // HostGrain's activation/projection code is unchanged from e13/3d2.
            // Combined with the frozen reader above, this exercises the deployed
            // adoption-before-projection behavior without building/deploying it.
            storage.RawReads.Clear();
            storage.Writes.Clear();
            using var rollback = CreateCluster();
            rollback.Deploy();
            foreach (var host in cases)
                await AssertActivationAndWritesAsync(rollback, store, storage, ids[host.Name], host);
            rollback.StopAllSilos();
        }
        finally
        {
            SiloConfigurator.DocumentStore = null;
            SiloConfigurator.Storage = null;
        }
    }

    private static async Task AssertActivationAndWritesAsync(
        TestCluster cluster, IDocumentStore store, RecordingJsonHostStorage storage, string id, HostCase host)
    {
        var grain = cluster.GrainFactory.GetGrain<IHostGrain>(id);
        var state = await grain.GetState();
        // The storage read really did rehydrate omitted Docker=0 as 10.
        // Activation must correct it before a scheduler or runtime projection.
        Assert.Equal((10, host.Tart, host.Native, host.Configured), storage.RawReads[id]);
        AssertConfiguration(state, host);
        Assert.Equal(host.Docker == 0 ? 1 : 0, storage.Writes.Count(write => write.Id == id));
        var document = (await store.Get<Host>(id))!;
        Assert.Equal((host.Docker, host.Tart, host.Native, host.Configured),
            (document.MaxDockerContainers, document.MaxTartVMs, document.MaxNativeProcesses, document.BackendLimitsConfigured));

        await grain.Register(host.Document.Name, host.Document.Platform, host.Document.Architecture, "test-version",
            new(host.Document.Labels));
        await grain.RecordHeartbeat("test-connection", 0, null);
        AssertConfiguration(await grain.GetState(), host);
        document = (await store.Get<Host>(id))!;
        Assert.Equal((host.Docker, host.Tart, host.Native, host.Configured),
            (document.MaxDockerContainers, document.MaxTartVMs, document.MaxNativeProcesses, document.BackendLimitsConfigured));
        Assert.True(document.IsApproved);
        Assert.NotNull(document.EnrolledAt);
        Assert.Equal(host.Document.EnrolledAt, document.EnrolledAt);
        Assert.Equal(host.Document.GroupId, document.GroupId);
        Assert.Equal(host.Document.Labels, document.Labels);
        Assert.Equal(host.Docker > 0, await grain.CanAcceptRunner(ExecutionBackend.Docker));
        var writes = storage.Writes.Where(write => write.Id == id).ToArray();
        Assert.NotEmpty(writes);
        Assert.All(writes, write =>
            Assert.Equal((host.Docker, host.Tart, host.Native, host.Configured), write.Configuration));
    }

    private static void AssertConfiguration(HostGrainState state, HostCase host)
    {
        Assert.Equal((host.Docker, host.Tart, host.Native, host.Configured),
            (state.MaxDockerContainers, state.MaxTartVMs, state.MaxNativeProcesses, state.BackendLimitsConfigured));
        Assert.Equal(host.Document.GroupId, state.GroupId);
        Assert.Equal(host.Document.Labels, state.Labels);
    }

    private static TestCluster CreateCluster()
    {
        var builder = new TestClusterBuilder();
        builder.Options.InitialSilosCount = 1;
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        return builder.Build();
    }

    private sealed record HostCase(string Name, Host Document, BinaryData Payload)
    {
        public int Docker => Document.MaxDockerContainers;
        public int Tart => Document.MaxTartVMs;
        public int Native => Document.MaxNativeProcesses;
        public bool Configured => Document.BackendLimitsConfigured;
    }

    private sealed class RecordingJsonHostStorage : IGrainStorage
    {
        public JsonGrainStorageSerializer Serializer { get; } =
            new(new OrleansJsonSerializer(Options.Create(new OrleansJsonSerializerOptions())));
        public ConcurrentDictionary<string, BinaryData> Payloads { get; } = new();
        public ConcurrentDictionary<string, (int, int, int, bool)> RawReads { get; } = new();
        public ConcurrentQueue<(string Id, (int, int, int, bool) Configuration)> Writes { get; } = new();

        public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            var id = grainId.Key.ToString();
            grainState.State = Serializer.Deserialize<T>(Payloads[id]);
            grainState.RecordExists = true;
            grainState.ETag = "test";
            if (grainState.State is HostGrainState state)
                RawReads[id] = (state.MaxDockerContainers, state.MaxTartVMs, state.MaxNativeProcesses, state.BackendLimitsConfigured);
            return Task.CompletedTask;
        }

        public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            var id = grainId.Key.ToString();
            Payloads[id] = Serializer.Serialize(grainState.State);
            grainState.RecordExists = true;
            grainState.ETag = "test";
            if (grainState.State is HostGrainState state)
                Writes.Enqueue((id, (state.MaxDockerContainers, state.MaxTartVMs, state.MaxNativeProcesses, state.BackendLimitsConfigured)));
            return Task.CompletedTask;
        }

        public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            Payloads.TryRemove(grainId.Key.ToString(), out _);
            grainState.RecordExists = false;
            grainState.ETag = null;
            return Task.CompletedTask;
        }
    }

    private sealed class SiloConfigurator : ISiloConfigurator
    {
        internal static IDocumentStore? DocumentStore { get; set; }
        internal static RecordingJsonHostStorage? Storage { get; set; }

        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder
                .AddMemoryGrainStorage("PubSubStore")
                .AddMemoryStreams("RunnerEvents")
                .ConfigureServices(services =>
                {
                    services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
                    services.AddSingleton(DocumentStore ?? throw new InvalidOperationException("Test store not configured."));
                    services.AddGrainStorage<RecordingJsonHostStorage>("PersistentStore",
                        (_, _) => Storage ?? throw new InvalidOperationException("Test storage not configured."));
                });
        }
    }

    private sealed class DeployedStateBinder : ISerializationBinder
    {
        private readonly DefaultSerializationBinder _fallback = new();

        public Type BindToType(string? assemblyName, string typeName) =>
            typeName == typeof(HostGrainState).FullName
                ? typeof(DeployedHostGrainState)
                : _fallback.BindToType(assemblyName, typeName);

        public void BindToName(Type serializedType, out string? assemblyName, out string? typeName) =>
            _fallback.BindToName(serializedType, out assemblyName, out typeName);
    }
}

// Frozen 3d2ee43/e13cfbc HostGrainState property contract; test-only type name.
public class DeployedHostGrainState
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
    [Id(24)] public bool BackendLimitsConfigured { get; set; }
}
