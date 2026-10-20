using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains.Events;
using RunnerRunner.Server.Grains.Interfaces;
using RunnerRunner.Server.Grains.State;
using RunnerRunner.Server.Services;
using Shiny.DocumentDb;
using System.Linq.Expressions;
using System.Text.RegularExpressions;

namespace RunnerRunner.Server.Grains;

// TODO: Add host-affinity grain placement so this grain activates on the silo
// whose "hostId" metadata matches the grain's primary key. Orleans 10 supports
// SiloMetadata was used by the retired HostWorker architecture. Options:
//   1. [SiloMetadataPlacement("hostId")] attribute if available
//   2. Custom IPlacementDirector that reads ISiloMetadataCache
// For now, grains can activate on the server silo; physical hosts connect as HostWorkers.
public class HostGrain : Grain, IHostGrain
{
    private static readonly Regex IpAddressRegex = new(@"\b\d{1,3}(?:\.\d{1,3}){3}\b", RegexOptions.Compiled);
    private readonly IPersistentState<HostGrainState> _state;
    private readonly ILogger<HostGrain> _logger;
    private readonly IServiceProvider _serviceProvider;
    private IGrainTimer? _heartbeatTimer;

    public HostGrain(
        [PersistentState("host", "PersistentStore")]
        IPersistentState<HostGrainState> state,
        ILogger<HostGrain> logger,
        IServiceProvider serviceProvider)
    {
        _state = state;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var host = await store.Get<Core.Models.Host>(this.GetPrimaryKeyString());
        if (host != null)
            await AdoptConfiguration(host);
    }

    public async Task Register(
        string name,
        HostPlatform platform,
        string? architecture,
        string agentVersion,
        Dictionary<string, string>? labels = null)
    {
        _state.State.Name = name;
        _state.State.Platform = platform;
        _state.State.Architecture = architecture;
        _state.State.AgentVersion = agentVersion;

        var registrationLabels = labels == null ? new Dictionary<string, string>() : new(labels);
        registrationLabels["os"] = platform.ToString().ToLowerInvariant();
        if (architecture is not null)
            registrationLabels["arch"] = architecture.ToLowerInvariant();
        foreach (var (key, value) in registrationLabels)
            _state.State.Labels[key] = value;

        _state.State.Status = AgentStatus.Online;
        if (_state.State.CreatedAt == default)
            _state.State.CreatedAt = DateTime.UtcNow;
        _state.State.LastHeartbeat = DateTime.UtcNow;

        await _state.WriteStateAsync();
        StartHeartbeatTimer();
        await SyncToDocumentDb(initialRegistration: true);
        await UpdateLabels(registrationLabels);
        await PublishHostStatusChange();

        _logger.LogInformation("Host {HostId} registered: {Name} ({Platform}/{Architecture})",
            this.GetPrimaryKeyString(), name, platform, architecture);
    }

    public async Task UpdateLabels(Dictionary<string, string> labels)
    {
        using var scope = _serviceProvider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var host = await store.Get<Core.Models.Host>(this.GetPrimaryKeyString());
        var merged = new Dictionary<string, string>(host?.Labels ?? _state.State.Labels);
        foreach (var (key, value) in labels)
            merged[key] = value;

        if (host != null)
        {
            host.Labels = merged;
            host.Capabilities = GetCapabilities(merged);
            host.UpdatedAt = DateTime.UtcNow;
            await HostDocumentUpdates.UpdateAsync(store, host, h => h.Labels, h => h.Capabilities, h => h.UpdatedAt);
        }

        _state.State.Labels = merged;
        await _state.WriteStateAsync();
    }

    public Task SetResourceLimits(int maxDocker, int maxTart, int maxNative)
        => UpdateConfigurationAsync(host =>
        {
            host.MaxDockerContainers = maxDocker;
            host.MaxTartVMs = maxTart;
            host.MaxNativeProcesses = maxNative;
            host.BackendLimitsConfigured = true;
        });

    public Task SetGroupId(string? groupId)
        => UpdateConfigurationAsync(host => host.GroupId = groupId);

    public Task SetConfiguration(int maxDocker, int maxTart, int maxNative, string? groupId, Dictionary<string, string>? labels = null)
        => UpdateConfigurationAsync(host =>
        {
            host.MaxDockerContainers = maxDocker;
            host.MaxTartVMs = maxTart;
            host.MaxNativeProcesses = maxNative;
            host.BackendLimitsConfigured = true;
            host.GroupId = groupId;
            if (labels != null)
                host.Labels = new Dictionary<string, string>(labels);
        }, requireExistingHost: true);

    private async Task UpdateConfigurationAsync(Action<Core.Models.Host> update, bool requireExistingHost = false)
    {
        using var scope = _serviceProvider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var hostId = this.GetPrimaryKeyString();
        var host = await store.Get<Core.Models.Host>(hostId);
        var exists = host != null;
        if (!exists && requireExistingHost)
            throw new InvalidOperationException($"Host '{hostId}' was not found.");

        host ??= new Core.Models.Host
        {
            Id = hostId,
            Name = _state.State.Name,
            MaxDockerContainers = _state.State.MaxDockerContainers,
            MaxTartVMs = _state.State.MaxTartVMs,
            MaxNativeProcesses = _state.State.MaxNativeProcesses,
            BackendLimitsConfigured = _state.State.BackendLimitsConfigured,
            GroupId = _state.State.GroupId,
            Labels = new Dictionary<string, string>(_state.State.Labels)
        };
        update(host);
        ArgumentOutOfRangeException.ThrowIfNegative(host.MaxDockerContainers);
        ArgumentOutOfRangeException.ThrowIfNegative(host.MaxTartVMs);
        ArgumentOutOfRangeException.ThrowIfNegative(host.MaxNativeProcesses);
        host.GroupId = string.IsNullOrWhiteSpace(host.GroupId) ? null : host.GroupId.Trim();
        host.UpdatedAt = DateTime.UtcNow;
        if (exists)
        {
            await HostDocumentUpdates.UpdateAsync(store, host,
                h => h.MaxDockerContainers, h => h.MaxTartVMs, h => h.MaxNativeProcesses,
                h => h.BackendLimitsConfigured, h => h.GroupId, h => h.Labels, h => h.UpdatedAt);
        }
        await AdoptConfiguration(host);
    }

    public async Task SetDraining(bool isDraining)
    {
        if (_state.State.IsDraining == isDraining)
            return;

        _state.State.IsDraining = isDraining;
        await _state.WriteStateAsync();
        await SyncToDocumentDb();
    }

    public async Task RecordHeartbeat(string connectionId, int runningCount, HostResourceUsage? resourceUsage)
    {
        _state.State.ConnectionId = connectionId;
        _state.State.LastHeartbeat = DateTime.UtcNow;
        _state.State.Status = AgentStatus.Online;
        ApplyResourceUsage(resourceUsage);
        await _state.WriteStateAsync();

        StartHeartbeatTimer();
        await PublishHostStatusChange();
        await SyncToDocumentDb();
    }

    public async Task MarkOffline()
    {
        _state.State.Status = AgentStatus.Offline;
        _state.State.ConnectionId = null;
        await _state.WriteStateAsync();

        _logger.LogWarning("Host {HostId} marked offline", this.GetPrimaryKeyString());
        await PublishHostStatusChange();
        await SyncToDocumentDb();
    }

    public Task<bool> CanAcceptRunner(ExecutionBackend backend)
    {
        if (_state.State.IsDraining)
            return Task.FromResult(false);

        var canAccept = backend switch
        {
            ExecutionBackend.Docker => _state.State.RunningDockerContainers < _state.State.MaxDockerContainers,
            ExecutionBackend.Tart => GetEffectiveRunningTartVMs() < _state.State.MaxTartVMs,
            ExecutionBackend.Native => _state.State.RunningNativeProcesses < _state.State.MaxNativeProcesses,
            _ => false
        };
        return Task.FromResult(canAccept);
    }

    public async Task IncrementRunningCount(ExecutionBackend backend)
    {
        switch (backend)
        {
            case ExecutionBackend.Docker: _state.State.RunningDockerContainers++; break;
            case ExecutionBackend.Tart: _state.State.RunningTartVMs++; break;
            case ExecutionBackend.Native: _state.State.RunningNativeProcesses++; break;
        }
        await _state.WriteStateAsync();
    }

    public async Task DecrementRunningCount(ExecutionBackend backend)
    {
        switch (backend)
        {
            case ExecutionBackend.Docker:
                _state.State.RunningDockerContainers = Math.Max(0, _state.State.RunningDockerContainers - 1);
                break;
            case ExecutionBackend.Tart:
                _state.State.RunningTartVMs = Math.Max(0, _state.State.RunningTartVMs - 1);
                break;
            case ExecutionBackend.Native:
                _state.State.RunningNativeProcesses = Math.Max(0, _state.State.RunningNativeProcesses - 1);
                break;
        }
        await _state.WriteStateAsync();
    }

    public Task<string?> GetConnectionId() => Task.FromResult(_state.State.ConnectionId);

    public Task<HostGrainState> GetState() => Task.FromResult(_state.State);

    private async Task PublishHostStatusChange()
    {
        var streamProvider = this.GetStreamProvider("RunnerEvents");
        var streamId = StreamId.Create("HostStatus", "all");
        var stream = streamProvider.GetStream<HostStatusChangedEvent>(streamId);
        await stream.OnNextAsync(new HostStatusChangedEvent
        {
            HostId = this.GetPrimaryKeyString(),
            HostName = _state.State.Name,
            Status = _state.State.Status
        });
    }

    private void StartHeartbeatTimer()
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = this.RegisterGrainTimer(
            OnHeartbeatTimeout,
            TimeSpan.FromSeconds(90),
            TimeSpan.FromSeconds(90));
    }

    private async Task OnHeartbeatTimeout(CancellationToken ct)
    {
        if (_state.State.Status == AgentStatus.Online)
        {
            _logger.LogWarning("Host {HostId} heartbeat timeout — marking offline", this.GetPrimaryKeyString());
            _state.State.Status = AgentStatus.Offline;
            _state.State.ConnectionId = null;
            await _state.WriteStateAsync();
            await PublishHostStatusChange();
            await SyncToDocumentDb();
        }

        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
    }

    /// <summary>
    /// Projects runtime fields only. Configuration is written by explicit UI/grain
    /// setters and read back here for scheduling; heartbeats never write it.
    /// </summary>
    private async Task SyncToDocumentDb(bool initialRegistration = false)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            var hostId = this.GetPrimaryKeyString();
            var allHosts = (await store.Query<Core.Models.Host>().ToList()).ToList();
            var existing = allHosts.FirstOrDefault(h => h.Id == hostId);
            var legacy = allHosts.FirstOrDefault(h => h.Id != hostId && string.Equals(h.Name, _state.State.Name, StringComparison.OrdinalIgnoreCase))
                ?? FindLegacyProjectionByIp(allHosts.Where(h => h.Id != hostId), hostId, _state.State.Name);

            if (existing == null)
            {
                existing = new Core.Models.Host
                {
                    Id = hostId,
                    Name = _state.State.Name,
                    MaxDockerContainers = _state.State.MaxDockerContainers,
                    MaxTartVMs = _state.State.MaxTartVMs,
                    MaxNativeProcesses = _state.State.MaxNativeProcesses,
                    BackendLimitsConfigured = _state.State.BackendLimitsConfigured,
                    GroupId = _state.State.GroupId,
                    EnrolledAt = initialRegistration ? DateTime.UtcNow : null,
                    CreatedAt = _state.State.CreatedAt == default ? DateTime.UtcNow : _state.State.CreatedAt
                };

                ApplyProjection(existing);
                existing.Capabilities = GetCapabilities(_state.State.Labels);
                existing.Labels = new Dictionary<string, string>(_state.State.Labels);
                if (legacy != null)
                {
                    CopyUserManagedFields(legacy, existing);
                    CopyConfiguration(legacy, existing);
                }
                else if (!existing.BackendLimitsConfigured)
                {
                    HostBackendLimitDefaults.ApplyToNewHost(existing);
                }

                await store.Insert(existing);

                if (legacy != null)
                {
                    await MigrateHostReferences(store, legacy.Id, hostId);
                    await store.Remove<Core.Models.Host>(legacy.Id);
                }
            }
            else
            {
                var firstEnrollment = initialRegistration && !existing.IsApproved && existing.EnrolledAt == null;
                if (legacy != null)
                {
                    CopyUserManagedFields(legacy, existing);
                    await HostDocumentUpdates.UpdateAsync(store, existing,
                        h => h.DisplayName, h => h.RunnerBasePath, h => h.WorkDirectory,
                        h => h.EnvironmentOverrides, h => h.ReportedEnvironment,
                        h => h.PendingHostWorkerUpdateSource, h => h.PendingHostWorkerUpdateVersion,
                        h => h.PendingHostWorkerUpdateAllowNonUpgrade, h => h.PendingHostWorkerUpdatePublicBaseUrl,
                        h => h.PendingHostWorkerUpdateQueuedAt, h => h.PendingHostWorkerUpdateDispatchedAt);
                    await MigrateHostReferences(store, legacy.Id, hostId);
                    await store.Remove<Core.Models.Host>(legacy.Id);
                }

                ApplyProjection(existing);
                Expression<Func<Core.Models.Host, object?>>[] properties =
                [
                    h => h.Name, h => h.Platform, h => h.Architecture, h => h.AgentVersion,
                    h => h.AgentStatus, h => h.LastHeartbeat,
                    h => h.ObservedRunningTartVMs, h => h.ObservedFreeDiskBytes, h => h.ObservedTotalDiskBytes,
                    h => h.ObservedDiskUsageAt, h => h.ObservedResourceUsageAt,
                    h => h.IsDraining, h => h.IsApproved, h => h.UpdatedAt
                ];
                if (initialRegistration)
                {
                    existing.Capabilities = GetCapabilities(_state.State.Labels);
                    existing.EnrolledAt ??= DateTime.UtcNow;
                    properties = [.. properties, h => h.Capabilities, h => h.EnrolledAt];
                    if (firstEnrollment && !existing.BackendLimitsConfigured)
                    {
                        HostBackendLimitDefaults.ApplyToNewHost(existing);
                        properties = [.. properties, h => h.MaxDockerContainers, h => h.MaxTartVMs, h => h.MaxNativeProcesses];
                    }
                }
                await HostDocumentUpdates.UpdateAsync(store, existing, properties);
            }

            await AdoptConfiguration(existing);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sync Host to DocumentDB");
            throw;
        }
    }

    private async Task AdoptConfiguration(Core.Models.Host host)
    {
        if (_state.State.MaxDockerContainers == host.MaxDockerContainers
            && _state.State.MaxTartVMs == host.MaxTartVMs
            && _state.State.MaxNativeProcesses == host.MaxNativeProcesses
            && _state.State.BackendLimitsConfigured == host.BackendLimitsConfigured
            && string.Equals(_state.State.GroupId, host.GroupId, StringComparison.Ordinal)
            && _state.State.Labels.Count == host.Labels.Count
            && host.Labels.All(label => _state.State.Labels.TryGetValue(label.Key, out var value) && value == label.Value))
        {
            return;
        }

        _state.State.MaxDockerContainers = host.MaxDockerContainers;
        _state.State.MaxTartVMs = host.MaxTartVMs;
        _state.State.MaxNativeProcesses = host.MaxNativeProcesses;
        _state.State.BackendLimitsConfigured = host.BackendLimitsConfigured;
        _state.State.GroupId = host.GroupId;
        _state.State.Labels = new Dictionary<string, string>(host.Labels);
        await _state.WriteStateAsync();
    }

    private static void CopyConfiguration(Core.Models.Host source, Core.Models.Host target)
    {
        target.MaxDockerContainers = source.MaxDockerContainers;
        target.MaxTartVMs = source.MaxTartVMs;
        target.MaxNativeProcesses = source.MaxNativeProcesses;
        target.BackendLimitsConfigured = source.BackendLimitsConfigured;
        target.GroupId = source.GroupId;
        target.Labels = new Dictionary<string, string>(source.Labels);
    }

    private void ApplyProjection(Core.Models.Host host)
    {
        host.Name = _state.State.Name;
        host.Platform = _state.State.Platform;
        host.Architecture = _state.State.Architecture;
        host.AgentVersion = _state.State.AgentVersion;
        host.AgentStatus = _state.State.Status;
        host.LastHeartbeat = _state.State.LastHeartbeat;
        host.ObservedRunningTartVMs = _state.State.ObservedRunningTartVMs;
        host.ObservedFreeDiskBytes = _state.State.ObservedFreeDiskBytes;
        host.ObservedTotalDiskBytes = _state.State.ObservedTotalDiskBytes;
        host.ObservedDiskUsageAt = _state.State.ObservedDiskUsageAt;
        host.ObservedResourceUsageAt = _state.State.ObservedResourceUsageAt;
        host.IsDraining = _state.State.IsDraining;
        host.IsApproved = true;
        host.UpdatedAt = DateTime.UtcNow;
    }

    private static List<string> GetCapabilities(Dictionary<string, string> labels)
        => labels
            .Where(kv => string.Equals(kv.Value, "true", StringComparison.OrdinalIgnoreCase)
                         && kv.Key is not "os" and not "arch" and not "role")
            .Select(kv => kv.Key)
            .OrderBy(k => k)
            .ToList();

    private void ApplyResourceUsage(HostResourceUsage? resourceUsage)
    {
        if (resourceUsage == null)
            return;

        var observed = false;

        if (resourceUsage.RunningTartVmCount is int runningTartVmCount)
        {
            _state.State.ObservedRunningTartVMs = Math.Max(0, runningTartVmCount);
            observed = true;
        }

        if (resourceUsage.FreeDiskBytes is long freeDiskBytes)
        {
            _state.State.ObservedFreeDiskBytes = Math.Max(0, freeDiskBytes);
            if (resourceUsage.TotalDiskBytes is long totalDiskBytes)
                _state.State.ObservedTotalDiskBytes = Math.Max(0, totalDiskBytes);
            _state.State.ObservedDiskUsageAt = DateTime.UtcNow;
            observed = true;
        }

        if (observed)
            _state.State.ObservedResourceUsageAt = DateTime.UtcNow;
    }

    private int GetEffectiveRunningTartVMs()
        => Math.Max(_state.State.RunningTartVMs, _state.State.ObservedRunningTartVMs ?? 0);

    private static void CopyUserManagedFields(Core.Models.Host source, Core.Models.Host target)
    {
        target.DisplayName ??= source.DisplayName;
        target.RunnerBasePath ??= source.RunnerBasePath;
        target.WorkDirectory ??= source.WorkDirectory;
        target.IsDraining = source.IsDraining;
        target.PendingHostWorkerUpdateSource ??= source.PendingHostWorkerUpdateSource;
        target.PendingHostWorkerUpdateVersion ??= source.PendingHostWorkerUpdateVersion;
        target.PendingHostWorkerUpdateAllowNonUpgrade = source.PendingHostWorkerUpdateAllowNonUpgrade;
        target.PendingHostWorkerUpdatePublicBaseUrl ??= source.PendingHostWorkerUpdatePublicBaseUrl;
        target.PendingHostWorkerUpdateQueuedAt ??= source.PendingHostWorkerUpdateQueuedAt;
        target.PendingHostWorkerUpdateDispatchedAt ??= source.PendingHostWorkerUpdateDispatchedAt;

        if (target.EnvironmentOverrides.Count == 0 && source.EnvironmentOverrides.Count > 0)
            target.EnvironmentOverrides = new Dictionary<string, string>(source.EnvironmentOverrides);

        if (target.ReportedEnvironment.Count == 0 && source.ReportedEnvironment.Count > 0)
            target.ReportedEnvironment = new Dictionary<string, string>(source.ReportedEnvironment);

        if (target.CreatedAt == default && source.CreatedAt != default)
            target.CreatedAt = source.CreatedAt;
    }

    private static async Task MigrateHostReferences(IDocumentStore store, string oldHostId, string newHostId)
    {
        if (string.Equals(oldHostId, newHostId, StringComparison.Ordinal))
            return;

        var assignments = (await store.Query<RunnerAssignment>().ToList())
            .Where(a => a.HostId == oldHostId)
            .ToList();
        foreach (var assignment in assignments)
        {
            assignment.HostId = newHostId;
            assignment.UpdatedAt = DateTime.UtcNow;
            await store.Update(assignment);
        }

        var instances = (await store.Query<RunnerInstance>().ToList())
            .Where(i => i.HostId == oldHostId)
            .ToList();
        foreach (var instance in instances)
        {
            instance.HostId = newHostId;
            await store.Update(instance);
        }

        var images = (await store.Query<AgentImage>().ToList())
            .Where(i => i.HostId == oldHostId)
            .ToList();
        foreach (var image in images)
        {
            image.HostId = newHostId;
            image.LastReportedAt = DateTime.UtcNow;
            await store.Update(image);
        }

        var rules = (await store.Query<ProvisioningRule>().ToList())
            .Where(r => r.TargetHostId == oldHostId)
            .ToList();
        foreach (var rule in rules)
        {
            rule.TargetHostId = newHostId;
            rule.UpdatedAt = DateTime.UtcNow;
            await store.Update(rule);
        }
    }

    private static Core.Models.Host? FindLegacyProjectionByIp(
        IEnumerable<Core.Models.Host> hosts,
        params string?[] values)
    {
        var ips = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .SelectMany(v => IpAddressRegex.Matches(v!).Cast<Match>().Select(m => m.Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ips.Count == 0)
            return null;

        return hosts
            .Where(h => ips.Any(ip =>
                (h.Name?.Contains(ip, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (h.DisplayName?.Contains(ip, StringComparison.OrdinalIgnoreCase) ?? false) ||
                h.Id.Contains(ip, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(h => h.LastHeartbeat ?? h.UpdatedAt)
            .FirstOrDefault();
    }
}
