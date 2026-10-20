using System.Collections.Concurrent;
using Shiny.DocumentDb;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Services;

/// <summary>
/// Processes reconciliation reports from HostWorkers, marking stale runners and cleaning up orphans.
/// </summary>
public class ReconciliationService : IHostedService, IDisposable
{
    // HostWorker sends a reconciliation report every 30s (see HostCommandProcessor), listing
    // only processes/containers that have actually launched. A Starting/Pending instance whose
    // deploy is merely slow (host under load, large runner package extraction, etc.) looks
    // identical to a failed one until it's had a real chance to finish — so give it the same
    // grace window RunnerInstanceGrain's own timers already allow before treating a missing
    // report as evidence of failure. Without this, a deploy that legitimately takes longer than
    // 30s gets marked stale and replaced on every single sweep.
    //
    // The two states get different windows because they're governed by different grain timers:
    // Pending -> StartPendingTimer (2 min); Starting -> StartRegistrationTimer (5 min). Dynamic
    // runners move from Pending to Starting almost immediately after Initialize (well before the
    // deploy command is even dispatched), so in practice nearly all deploy latency is spent in
    // Starting — a 2-minute grace period there is shorter than a real-world slow deploy can take
    // (observed live: ~3.5 minutes for a native macOS runner under host load).
    private static readonly TimeSpan PendingDeployGracePeriod = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StartingDeployGracePeriod = TimeSpan.FromMinutes(5);

    // A resource the host reports running but RunnerRunner has no record of is cleaned up only
    // after it has been reported that way continuously for this long. Records disappear for
    // reasons that say nothing about the resource: a recycle path removed the record right
    // after asking the host to stop it (that stop is still in flight), or a record was
    // overwritten while the server could not see the host. Cleaning up on the first report
    // killed live jobs in both cases.
    internal static readonly TimeSpan RunningOrphanGracePeriod = TimeSpan.FromMinutes(10);

    // A running orphan the provider cannot answer about (Unknown) is left alone this long,
    // counted from the first report that listed it. It is the same ceiling RunnerTimeoutService
    // gives a tracked runner (twice DynamicProvisioning:RunningTimeoutMinutes), so a runner
    // that loses its record is not destroyed sooner than one that kept it.
    private readonly TimeSpan _orphanUnknownActivityCeiling;

    // host|backend|resource -> first report that listed it as a running orphan.
    private readonly ConcurrentDictionary<string, DateTime> _runningOrphanFirstSeen = new(StringComparer.Ordinal);

    // Instance id -> a Busy provider answer, reused for
    // RunnerTimeoutService.ProviderActivityCacheDuration. A runner left running because the
    // provider reports it busy is missing from every 30s report; this keeps that to one
    // provider lookup every couple of minutes. Unknown is not cached: it ends the record.
    private readonly ConcurrentDictionary<string, (ProviderRunnerActivity Activity, DateTime CheckedAt)> _blockingActivity =
        new(StringComparer.Ordinal);

    // host|backend|resource -> the same kind of blocking answer for a running orphan, so an
    // orphan past its grace period costs one provider lookup every couple of minutes rather
    // than one per report.
    private readonly ConcurrentDictionary<string, (ProviderRunnerActivity Activity, DateTime CheckedAt)> _orphanBlockingActivity =
        new(StringComparer.Ordinal);

    private readonly ILogger<ReconciliationService> _logger;
    private readonly IServiceProvider _services;
    private readonly IHostCommandDispatcher _hostCommands;

    public ReconciliationService(
        ILogger<ReconciliationService> logger,
        IServiceProvider services,
        IHostCommandDispatcher hostCommands,
        IConfiguration configuration)
    {
        _logger = logger;
        _services = services;
        _hostCommands = hostCommands;
        _orphanUnknownActivityCeiling = DynamicRunnerTimeouts.GetUnknownActivityCeiling(configuration);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        StreamSubscriptionService.OnReconciliationReportReceived += HandleReconciliation;
        _logger.LogInformation("ReconciliationService started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        StreamSubscriptionService.OnReconciliationReportReceived -= HandleReconciliation;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        StreamSubscriptionService.OnReconciliationReportReceived -= HandleReconciliation;
    }

    private async void HandleReconciliation(ReconciliationReport report)
    {
        try
        {
            using var scope = _services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            var registrationCleanup = scope.ServiceProvider.GetRequiredService<RunnerRegistrationCleanupService>();

            await ProcessReportAsync(report, store, registrationCleanup, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing reconciliation report from {Host}", report.HostId);
        }
    }

    internal async Task ProcessReportAsync(
        ReconciliationReport report,
        IDocumentStore store,
        RunnerRegistrationCleanupService registrationCleanup,
        DateTime nowUtc)
    {
        var reportHostId = report.HostId;
        var hosts = (await store.Query<Host>().ToList())
            .Where(h => h.Id == reportHostId || h.Name == reportHostId)
            .ToList();
        if (hosts.Count == 0)
        {
            _logger.LogWarning("No host found for reconciliation report from {Host}", reportHostId);
            return;
        }
        var hostId = hosts.First().Id;

        // Load all runner instances for this host
        var dbInstances = (await store.Query<RunnerInstance>().ToList())
            .Where(i => i.HostId == hostId)
            .ToList();

        // Find stale DB records (in DB but NOT in report)
        foreach (var instance in dbInstances)
        {
            if (instance.Status is not (RunnerInstanceStatus.Running
                or RunnerInstanceStatus.Starting
                or RunnerInstanceStatus.Pending))
                continue;

            var matchedRunner = report.Runners.FirstOrDefault(r => MatchesRunner(instance, r));

            if (matchedRunner == null)
            {
                if (IsWithinDeployGracePeriod(instance.Status, instance.DeployedAt ?? instance.CreatedAt, nowUtc))
                    continue;

                if (CouldNotSee(instance, report))
                {
                    _logger.LogDebug(
                        "Runner {Name} is missing from host {Host}'s report, but the report cannot show whether it exited (backend not listed, or a HostWorker that predates backend availability); leaving it {Status}",
                        instance.RunnerName, reportHostId, instance.Status);
                    continue;
                }

                // The host listed the backend and the runner is not there, but a runner the
                // provider reports busy is evidently alive somewhere (a discovery gap on the
                // host, say). Ending it would requeue its job and deregister it mid-job.
                var activity = await GetProviderActivityAsync(store, registrationCleanup, instance, nowUtc);
                if (activity == ProviderRunnerActivity.Busy)
                {
                    _logger.LogWarning(
                        "Runner {Name} is missing from host {Host}'s report, but the provider reports it busy; leaving it {Status} for a later report or the timeout service to decide",
                        instance.RunnerName, reportHostId, instance.Status);
                    continue;
                }

                var newStatus = instance.Status == RunnerInstanceStatus.Running
                    ? RunnerInstanceStatus.Crashed
                    : RunnerInstanceStatus.Stopped;

                instance.Status = newStatus;
                instance.StatusMessage = "Host no longer reported the runner";
                instance.StoppedAt = nowUtc;
                instance.HostReportedGoneAt = nowUtc;
                await store.Update(instance);

                await RequeueDynamicJobAsync(
                    store,
                    instance,
                    "Runner disappeared from the host before the queued job started; provisioning will be retried");

                _logger.LogWarning(
                    "Marking stale runner {Name} as {Status} — not found on host",
                    instance.RunnerName, newStatus);

                await TryDeregisterRunnerAsync(store, registrationCleanup, instance, activity);
            }
            else if (!IsRunnerStillActive(matchedRunner, instance, nowUtc))
            {
                // A runner the provider reports busy is on a job somewhere, whatever the host
                // says about this process (a stale or misread status, say). Ending it would
                // requeue its job, clean up its resource and deregister it mid-job.
                var activity = await GetProviderActivityAsync(store, registrationCleanup, instance, nowUtc);
                if (activity == ProviderRunnerActivity.Busy)
                {
                    _logger.LogWarning(
                        "Host {Host} reports runner {Name} exited ({HostStatus}), but the provider reports it busy; leaving it {Status} for a later report or the timeout service to decide",
                        reportHostId, instance.RunnerName, matchedRunner.Status, instance.Status);
                    continue;
                }

                instance.Status = RunnerInstanceStatus.Stopped;
                instance.StatusMessage = $"Host reported runner exited ({matchedRunner.Status})";
                instance.StoppedAt = nowUtc;
                instance.HostReportedGoneAt = nowUtc;
                await store.Update(instance);

                _logger.LogInformation(
                    "Marking runner {Name} as stopped — host reported it exited (status: {HostStatus})",
                    instance.RunnerName, matchedRunner.Status);

                await RequeueDynamicJobAsync(
                    store,
                    instance,
                    $"Runner exited on the host before the queued job started ({matchedRunner.Status}); provisioning will be retried");
                await SendCleanupCommandAsync(hostId, matchedRunner);

                // Unknown still ends the record (the host saw the process exit), but only Idle
                // or NotRegistered lets the registration go.
                await TryDeregisterRunnerAsync(store, registrationCleanup, instance, activity);
            }
        }

        foreach (var (cachedId, cached) in _blockingActivity)
        {
            if (nowUtc - cached.CheckedAt >= RunnerTimeoutService.ProviderActivityCacheDuration)
                _blockingActivity.TryRemove(cachedId, out _);
        }

        // Find orphans on host (in report but NOT in DB)
        List<WebhookEvent>? inProgressEvents = null;
        var runningOrphanKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var runner in report.Runners)
        {
            if (dbInstances.Any(i => MatchesRunner(i, runner)))
                continue;

            var resource = DescribeResource(runner);

            if (!IsRunnerStillActive(runner))
            {
                _logger.LogWarning(
                    "Sending cleanup for orphaned {Backend} resource {Resource} on host {Host} (running: {Running}, status: {Status})",
                    runner.Backend, resource, reportHostId, runner.IsRunning, runner.Status);
                await SendCleanupCommandAsync(hostId, runner);
                continue;
            }

            var key = $"{hostId}|{runner.Backend}|{resource}";
            runningOrphanKeys.Add(key);
            var firstSeen = _runningOrphanFirstSeen.GetOrAdd(key, nowUtc);
            if (nowUtc - firstSeen < RunningOrphanGracePeriod)
            {
                if (firstSeen == nowUtc)
                {
                    _logger.LogInformation(
                        "Host {Host} reports {Backend} resource {Resource} ({RunnerName}) running with no matching record; cleaning it up only if that persists for {Grace}",
                        reportHostId, runner.Backend, resource, runner.RunnerName, RunningOrphanGracePeriod);
                }
                continue;
            }

            // A container Docker has created but never started has no process, so it cannot
            // be on a job, and one still "created" after the grace period never will be.
            if (IsDockerCreated(runner))
            {
                _logger.LogWarning(
                    "Sending cleanup for orphaned Docker container {Resource} on host {Host}: never started (status: {Status}) since {FirstSeen:u}",
                    resource, reportHostId, runner.Status, firstSeen);
                await SendCleanupCommandAsync(hostId, runner);
                _runningOrphanFirstSeen.TryRemove(key, out _);
                _orphanBlockingActivity.TryRemove(key, out _);
                continue;
            }

            try
            {
                inProgressEvents ??= (await store.Query<WebhookEvent>().ToList())
                    .Where(e => string.Equals(e.Status, "in_progress", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(e.AssignedRunnerName))
                    .ToList();
            }
            catch (Exception ex)
            {
                // Without the job lookup we cannot rule out that this runner is building a
                // job, so leave it for the next report rather than guess.
                _logger.LogWarning(ex,
                    "Not cleaning up {Backend} resource {Resource} on host {Host} yet: could not check whether runner {RunnerName} is on a job",
                    runner.Backend, resource, reportHostId, runner.RunnerName);
                continue;
            }
            var liveJob = inProgressEvents.FirstOrDefault(e =>
                string.Equals(e.AssignedRunnerName, runner.RunnerName, StringComparison.OrdinalIgnoreCase));
            if (liveJob != null)
            {
                _logger.LogWarning(
                    "Not cleaning up {Backend} resource {Resource} on host {Host}: it has no record, but runner {RunnerName} is running job {JobId}",
                    runner.Backend, resource, reportHostId, runner.RunnerName, liveJob.JobId);
                continue;
            }

            // No local record of a job is not evidence the runner is idle: webhooks get lost.
            // Only the provider saying it is idle or gone (or, for a provider that cannot say,
            // a long enough wait) justifies destroying a resource the host reports running.
            // A cached answer may only keep the resource: a Busy one, or an Unknown one while
            // Unknown cannot let it go. Past the ceiling an Unknown cached a minute earlier
            // could hide that the provider now reports the runner busy, so ask again.
            var pastUnknownCeiling = nowUtc - firstSeen > _orphanUnknownActivityCeiling;
            ProviderRunnerActivity? activity;
            if (_orphanBlockingActivity.TryGetValue(key, out var cachedOrphan)
                && nowUtc - cachedOrphan.CheckedAt < RunnerTimeoutService.ProviderActivityCacheDuration
                && (cachedOrphan.Activity == ProviderRunnerActivity.Busy || !pastUnknownCeiling))
            {
                activity = cachedOrphan.Activity;
            }
            else
            {
                try
                {
                    activity = await registrationCleanup.GetOrphanRunnerActivityAsync(store, runner);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "Not cleaning up {Backend} resource {Resource} on host {Host}: could not ask the provider about runner {RunnerName}",
                        runner.Backend, resource, reportHostId, runner.RunnerName);
                    continue;
                }

                if (activity is ProviderRunnerActivity.Busy or ProviderRunnerActivity.Unknown)
                    _orphanBlockingActivity[key] = (activity.Value, nowUtc);
                else
                    _orphanBlockingActivity.TryRemove(key, out _);
            }

            if (activity is not { } known)
            {
                _logger.LogWarning(
                    "Not cleaning up {Backend} resource {Resource} on host {Host}: no profile or credential could be resolved for runner {RunnerName}",
                    runner.Backend, resource, reportHostId, runner.RunnerName);
                continue;
            }

            if (!RunnerTimeoutService.ProviderAllowsRecycle(known)
                && !(known == ProviderRunnerActivity.Unknown && pastUnknownCeiling))
            {
                _logger.LogWarning(
                    "Not cleaning up {Backend} resource {Resource} on host {Host}: it has no record, but the provider reports runner {RunnerName} {Activity}",
                    runner.Backend, resource, reportHostId, runner.RunnerName, known);
                continue;
            }

            _logger.LogWarning(
                "Sending cleanup for orphaned {Backend} resource {Resource} on host {Host} (running: {Running}, status: {Status}, orphaned since {FirstSeen:u}, provider: {Activity})",
                runner.Backend, resource, reportHostId, runner.IsRunning, runner.Status, firstSeen, known);
            await SendCleanupCommandAsync(hostId, runner);
            _runningOrphanFirstSeen.TryRemove(key, out _);
            _orphanBlockingActivity.TryRemove(key, out _);
        }

        // Forget resources this host no longer reports as running orphans, so a later
        // orphan reusing a name or PID starts its grace period from scratch.
        var hostPrefix = hostId + "|";
        foreach (var key in _runningOrphanFirstSeen.Keys)
        {
            if (key.StartsWith(hostPrefix, StringComparison.Ordinal) && !runningOrphanKeys.Contains(key))
                _runningOrphanFirstSeen.TryRemove(key, out _);
        }
        foreach (var (key, cached) in _orphanBlockingActivity)
        {
            if ((key.StartsWith(hostPrefix, StringComparison.Ordinal) && !runningOrphanKeys.Contains(key))
                || nowUtc - cached.CheckedAt >= RunnerTimeoutService.ProviderActivityCacheDuration)
            {
                _orphanBlockingActivity.TryRemove(key, out _);
            }
        }

        // Update health for matched runners, and record that the host no longer runs the
        // resource of a record that already ended (RunnerTimeoutService forgets those only
        // on that evidence, never on stale health).
        foreach (var instance in dbInstances)
        {
            var matched = report.Runners.FirstOrDefault(r => MatchesRunner(instance, r));

            if (matched != null && IsRunnerStillActive(matched, instance, nowUtc))
            {
                instance.LastHealthCheck = nowUtc;
                await store.Update(instance);
            }
            else if (instance.Status is RunnerInstanceStatus.Stopped or RunnerInstanceStatus.Failed or RunnerInstanceStatus.Crashed
                && !RunnerTimeoutService.HostReportedGone(instance)
                && !CouldNotSee(instance, report))
            {
                instance.HostReportedGoneAt = nowUtc;
                await store.Update(instance);
            }
        }
    }

    /// <summary>
    /// True when <paramref name="report"/> cannot show whether <paramref name="instance"/>'s
    /// resource is gone: its backend could not be listed, or the sender predates
    /// <see cref="ReconciliationReport.UnavailableBackends"/> and so cannot say.
    /// </summary>
    internal static bool CouldNotSee(RunnerInstance instance, ReconciliationReport report) =>
        !report.ReportsBackendAvailability || WasNotListed(instance, report.UnavailableBackends);

    /// <summary>
    /// The handle that names <paramref name="runner"/> to its backend. Docker reports
    /// <see cref="DiscoveredRunnerInfo.ContainerId"/> as an empty string (not null) for
    /// Tart and native resources, so a null-coalescing chain stops there.
    /// </summary>
    internal static string DescribeResource(DiscoveredRunnerInfo runner) => runner.Backend switch
    {
        ExecutionBackend.Docker when !string.IsNullOrWhiteSpace(runner.ContainerId) => runner.ContainerId,
        ExecutionBackend.Tart when !string.IsNullOrWhiteSpace(runner.VmName) => runner.VmName,
        ExecutionBackend.Native when runner.ProcessId.HasValue => runner.ProcessId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => FirstNonBlank(runner.ContainerId, runner.VmName, runner.ProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture), runner.RunnerName, runner.InstanceId) ?? "(unidentified)"
    };

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    internal static CleanupOrphanCommand BuildCleanupCommand(DiscoveredRunnerInfo runner) => new()
    {
        Backend = runner.Backend,
        ContainerId = string.IsNullOrWhiteSpace(runner.ContainerId) ? null : runner.ContainerId,
        VmName = string.IsNullOrWhiteSpace(runner.VmName) ? null : runner.VmName,
        ProcessId = runner.ProcessId,
        InstanceDir = string.IsNullOrWhiteSpace(runner.InstanceDir) ? null : runner.InstanceDir
    };

    /// <summary>
    /// True if an instance in <paramref name="status"/>, deployed/created at <paramref name="referenceTime"/>,
    /// should still be given the benefit of the doubt for not appearing in a reconciliation report as of
    /// <paramref name="nowUtc"/>. Only Pending/Starting get a grace window (matching RunnerInstanceGrain's own
    /// PendingTimer/RegistrationTimer) — Running never does, since a runner that was confirmed running and then
    /// disappeared has genuinely crashed, not merely deployed slowly.
    /// </summary>
    internal static bool IsWithinDeployGracePeriod(RunnerInstanceStatus status, DateTime referenceTime, DateTime nowUtc)
    {
        var gracePeriod = status switch
        {
            RunnerInstanceStatus.Pending => PendingDeployGracePeriod,
            RunnerInstanceStatus.Starting => StartingDeployGracePeriod,
            _ => TimeSpan.Zero
        };

        return nowUtc - referenceTime < gracePeriod;
    }

    internal static bool MatchesRunner(RunnerInstance instance, DiscoveredRunnerInfo runner)
    {
        if (!string.IsNullOrEmpty(runner.InstanceId) && runner.InstanceId == instance.Id)
            return true;

        if (!string.IsNullOrEmpty(runner.ContainerId) && runner.ContainerId == instance.ContainerId)
            return true;

        if (!string.IsNullOrEmpty(runner.VmName) && runner.VmName == instance.VmName)
            return true;

        if (runner.ProcessId.HasValue && runner.ProcessId == instance.ProcessId)
            return true;

        return !string.IsNullOrEmpty(runner.RunnerName)
            && string.Equals(runner.RunnerName, instance.RunnerName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the host said it could not list the backend that runs <paramref name="instance"/>,
    /// so the runner's absence from the report says nothing about whether it exited. An
    /// instance with no handle yet could be on any backend.
    /// </summary>
    internal static bool WasNotListed(RunnerInstance instance, IReadOnlyCollection<ExecutionBackend>? unavailableBackends)
    {
        if (unavailableBackends is not { Count: > 0 })
            return false;

        ExecutionBackend? backend =
            !string.IsNullOrWhiteSpace(instance.ContainerId) ? ExecutionBackend.Docker
            : !string.IsNullOrWhiteSpace(instance.VmName) ? ExecutionBackend.Tart
            : instance.ProcessId.HasValue ? ExecutionBackend.Native
            : null;
        return backend is not { } known || unavailableBackends.Contains(known);
    }

    // Whether the host still has the runner's resource, running or about to run. Docker lists
    // a container "created" between DockerBackend creating it and starting it (it copies the
    // start script in first), and "restarting" or "paused" ones will run again; only exited,
    // dead or removing ones have ended. A HostWorker reports IsRunning false for all of them,
    // so the Docker state is read here, on the server, which also covers older HostWorkers.
    // Treating "created" as exited ended JIT runners as they started (Tower 2026-10-20
    // 07:58:39: "host reported it exited (status: created)"). A "created" container has no
    // process, though, so it is only starting while its deploy is: a recorded one is judged
    // by the overload below, and an untracked one is cleaned up after the running-orphan
    // grace period in ProcessReportAsync.
    internal static bool IsRunnerStillActive(DiscoveredRunnerInfo runner) =>
        runner.IsRunning
        || string.Equals(runner.Status, "running", StringComparison.OrdinalIgnoreCase)
        || (runner.Backend == ExecutionBackend.Docker && runner.Status?.Trim() is { } state && DockerStatesNotEnded.Contains(state));

    // As above, for the resource that matches a record. A "created" container counts as
    // starting only for StartingDeployGracePeriod from the record's deploy: the window every
    // other path gives a deploy (the missing-runner grace, the Starting timeout and the
    // grain's registration timer), counted from DeployedAt, which precedes the image pull.
    // Past it the container has ended. Otherwise a static record failed at the Starting
    // timeout kept matching it and refreshing its health, so neither the record nor the
    // container was ever removed.
    internal static bool IsRunnerStillActive(DiscoveredRunnerInfo runner, RunnerInstance instance, DateTime nowUtc) =>
        IsRunnerStillActive(runner)
        && !(IsDockerCreated(runner) && nowUtc - (instance.DeployedAt ?? instance.CreatedAt) >= StartingDeployGracePeriod);

    private static bool IsDockerCreated(DiscoveredRunnerInfo runner) =>
        runner.Backend == ExecutionBackend.Docker
        && !runner.IsRunning
        && string.Equals(runner.Status?.Trim(), "created", StringComparison.OrdinalIgnoreCase);

    private static readonly HashSet<string> DockerStatesNotEnded =
        new(["created", "restarting", "paused"], StringComparer.OrdinalIgnoreCase);

    internal static bool TryPrepareDynamicWebhookRetry(
        RunnerInstance instance,
        WebhookEvent? linkedEvent,
        DateTime nowUtc,
        string reason)
        => string.Equals(instance.ProvisioningMode, "dynamic", StringComparison.OrdinalIgnoreCase)
            && RunnerTimeoutService.PrepareLinkedEventRetry(linkedEvent, nowUtc, reason);

    private static async Task RequeueDynamicJobAsync(
        IDocumentStore store,
        RunnerInstance instance,
        string reason)
    {
        if (string.Equals(instance.ProvisioningMode, "dynamic", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(instance.WebhookEventId))
        {
            var linkedEvent = await store.Get<WebhookEvent>(instance.WebhookEventId);
            if (TryPrepareDynamicWebhookRetry(instance, linkedEvent, DateTime.UtcNow, reason) && linkedEvent != null)
                await store.Update(linkedEvent);
        }
    }

    /// <summary>
    /// Deregisters <paramref name="instance"/>'s runner only when the provider allows it.
    /// Deregistering a runner that is on a job kills the job, so Busy and Unknown keep the
    /// registration.
    /// </summary>
    private async Task TryDeregisterRunnerAsync(
        IDocumentStore store,
        RunnerRegistrationCleanupService registrationCleanup,
        RunnerInstance instance,
        ProviderRunnerActivity activity)
    {
        if (RunnerTimeoutService.ProviderAllowsRecycle(activity))
        {
            await registrationCleanup.TryRemoveRunnerAsync(store, instance);
            return;
        }

        if (activity == ProviderRunnerActivity.Busy)
        {
            _logger.LogWarning(
                "Not deregistering runner {Name} ({Id}): the provider reports it busy",
                instance.RunnerName, instance.Id);
        }
        else
        {
            _logger.LogDebug(
                "Not deregistering runner {Name} ({Id}): the provider reports it {Activity}",
                instance.RunnerName, instance.Id, activity);
        }
    }

    private async Task<ProviderRunnerActivity> GetProviderActivityAsync(
        IDocumentStore store,
        RunnerRegistrationCleanupService registrationCleanup,
        RunnerInstance instance,
        DateTime nowUtc)
    {
        if (_blockingActivity.TryGetValue(instance.Id, out var cached)
            && nowUtc - cached.CheckedAt < RunnerTimeoutService.ProviderActivityCacheDuration)
        {
            return cached.Activity;
        }

        ProviderRunnerActivity activity;
        try
        {
            activity = await registrationCleanup.GetProviderRunnerActivityAsync(store, instance);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not determine provider activity for runner {RunnerName}", instance.RunnerName);
            activity = ProviderRunnerActivity.Unknown;
        }

        // Only Busy is reused: both callers end the record on Unknown, so a cached Unknown
        // would end a runner the provider may since have given a job (a record set Running
        // again by a RunnerStarted event, say) without asking it.
        if (activity == ProviderRunnerActivity.Busy)
            _blockingActivity[instance.Id] = (activity, nowUtc);
        else
            _blockingActivity.TryRemove(instance.Id, out _);

        return activity;
    }

    private Task SendCleanupCommandAsync(string hostId, DiscoveredRunnerInfo runner) =>
        _hostCommands.DispatchCleanupOrphanAsync(hostId, BuildCleanupCommand(runner));
}
