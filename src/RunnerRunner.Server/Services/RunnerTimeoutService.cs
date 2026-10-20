using System.Collections.Concurrent;
using Shiny.DocumentDb;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Services;

/// <summary>
/// Periodically scans RunnerInstance records and transitions stuck instances
/// to Failed or Crashed based on timeout rules.
/// </summary>
public class RunnerTimeoutService : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PendingTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StartingTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DynamicPickupTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StoppingTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HealthCheckStaleTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan DynamicTerminalRetention = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CompletedWebhookRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan StaleNoMatchRetention = TimeSpan.FromHours(24);
    private static readonly TimeSpan IgnoredWebhookRetention = TimeSpan.FromDays(2);
    private static readonly TimeSpan RetryAfterRunnerFailureDelay = TimeSpan.FromSeconds(10);

    // How long a provider answer that blocks a stop (Busy, Unknown) is reused. The scan runs
    // every 30s and each lookup costs up to two GitHub API calls per runner; a busy runner
    // stays busy for minutes, and acting on Idle is never delayed because Idle is not cached.
    internal static readonly TimeSpan ProviderActivityCacheDuration = TimeSpan.FromMinutes(2);

    // "pending" (WebhookEvent.ScheduleRetry's default status) is one of the open-ended queue
    // statuses that WebhookEvent.EnsureLifecycleWindow/ScheduleRetry deliberately never expire
    // (they're for legitimate no-fixed-SLA waits like FIFO/capacity ordering). Requeuing a linked
    // event through that same "pending" status after a runner-launch failure inherits that
    // no-expiry behavior, so a deterministically-broken host/image (e.g. a runner that exits
    // immediately on every attempt) gets respawned every ~10s forever instead of ever giving up.
    // Cap consecutive runner-launch failures for one event instead of relying on ExpiresAt.
    private const int MaxConsecutiveRunnerFailureRetries = 5;

    private readonly ILogger<RunnerTimeoutService> _logger;
    private readonly IServiceProvider _services;
    private readonly IHostCommandDispatcher _hostCommands;
    private readonly ConcurrentDictionary<string, (ProviderRunnerActivity Activity, DateTime CheckedAt)> _blockingActivity =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Backstop for dynamic runners whose completion webhook was lost; see
    /// <see cref="DynamicRunnerTimeouts.GetRunningTimeout"/>.
    /// </summary>
    private readonly TimeSpan _dynamicRunningTimeout;

    public RunnerTimeoutService(
        ILogger<RunnerTimeoutService> logger,
        IServiceProvider services,
        IHostCommandDispatcher hostCommands,
        IConfiguration configuration)
    {
        _logger = logger;
        _services = services;
        _hostCommands = hostCommands;

        _dynamicRunningTimeout = DynamicRunnerTimeouts.GetRunningTimeout(configuration);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RunnerTimeoutService started");

        try
        {
            using var timer = new PeriodicTimer(ScanInterval);

            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await ScanForTimeoutsAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during runner timeout scan");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        _logger.LogInformation("RunnerTimeoutService stopped");
    }

    private async Task ScanForTimeoutsAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var registrations = scope.ServiceProvider.GetService<RunnerRegistrationCleanupService>();

        var instances = (await store.Query<RunnerInstance>().ToList()).ToList();
        var now = DateTime.UtcNow;

        foreach (var instance in instances)
        {
            ct.ThrowIfCancellationRequested();

            switch (instance.Status)
            {
                case RunnerInstanceStatus.Pending:
                    if (await CheckPendingTimeout(store, instance, now))
                        continue;
                    break;
                case RunnerInstanceStatus.Starting:
                    if (await CheckStartingTimeout(store, instance, now))
                        continue;
                    break;
                case RunnerInstanceStatus.Running:
                    if (await CheckRunningTimeouts(store, registrations, instance, now, ct))
                        continue;
                    break;
                case RunnerInstanceStatus.Stopping:
                    await CheckStoppingTimeout(store, instance, now);
                    break;
            }

            await CleanupExpiredDynamicRecord(store, registrations, instance, now, ct);
        }

        var knownIds = instances.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var cachedId in _blockingActivity.Keys)
        {
            if (!knownIds.Contains(cachedId))
                _blockingActivity.TryRemove(cachedId, out _);
        }

        await CleanupExpiredWebhookEvents(store, now, ct);
    }

    /// <returns>True when the instance's record was removed or must be left as it is.</returns>
    private async Task<bool> CheckPendingTimeout(IDocumentStore store, RunnerInstance instance, DateTime now)
    {
        var referenceTime = instance.DeployedAt ?? instance.CreatedAt;
        if (now - referenceTime <= PendingTimeout)
            return false;

        _logger.LogWarning(
            "Runner {RunnerName} ({Id}) timed out in Pending state after {Elapsed}",
            instance.RunnerName, instance.Id, now - referenceTime);

        // A dynamic runner is stopped before it is failed and its job requeued: if the stop
        // cannot be sent, a replacement would race a runner that may yet come up and take a
        // job. The record stays Pending and the next scan retries.
        if (instance.ProvisioningMode == "dynamic" && !await TrySendStopRunner(store, instance))
            return true;

        instance.Status = RunnerInstanceStatus.Failed;
        instance.StatusMessage = "Deploy timeout - HostWorker did not acknowledge";
        await store.Update(instance);

        if (instance.ProvisioningMode == "dynamic")
        {
            await TryRequeueLinkedWebhookEvent(
                store,
                instance,
                now,
                "Runner deployment timed out before the host acknowledged it; retrying provisioning");
            await store.Remove<RunnerInstance>(instance.Id);
            return true;
        }

        return false;
    }

    /// <returns>True when the instance's record was removed or must be left as it is.</returns>
    private async Task<bool> CheckStartingTimeout(IDocumentStore store, RunnerInstance instance, DateTime now)
    {
        var referenceTime = instance.StartedAt ?? instance.DeployedAt;
        if (referenceTime == null || now - referenceTime.Value <= StartingTimeout)
            return false;

        _logger.LogWarning(
            "Runner {RunnerName} ({Id}) timed out in Starting state after {Elapsed}",
            instance.RunnerName, instance.Id, now - referenceTime.Value);

        if (instance.ProvisioningMode == "dynamic" && !await TrySendStopRunner(store, instance))
            return true;

        instance.Status = RunnerInstanceStatus.Failed;
        instance.StatusMessage = "Registration timeout — runner did not connect to provider";
        await store.Update(instance);

        if (instance.ProvisioningMode == "dynamic")
        {
            await TryRequeueLinkedWebhookEvent(
                store,
                instance,
                now,
                "Runner registration timed out before it connected to the provider; retrying provisioning");
            await store.Remove<RunnerInstance>(instance.Id);
            return true;
        }

        return false;
    }

    /// <returns>True when the instance's record was removed.</returns>
    private async Task<bool> CheckRunningTimeouts(
        IDocumentStore store,
        RunnerRegistrationCleanupService? registrations,
        RunnerInstance instance,
        DateTime now,
        CancellationToken ct)
    {
        if (instance.ProvisioningMode == "dynamic"
            && !string.IsNullOrWhiteSpace(instance.WebhookEventId))
        {
            var linkedEvent = await store.Get<WebhookEvent>(instance.WebhookEventId);
            var eventStatus = linkedEvent?.Status?.Trim();
            var binding = ClassifyLinkedJob(instance, linkedEvent);

            if (eventStatus is "completed" or "timed_out" or "ignored" or WebhookEvent.StatusIgnoredScope or WebhookEvent.StatusIgnoredTarget or "rejected")
            {
                // The linked event resolving only justifies stopping this runner if this
                // runner is the one that served it. GitHub may hand a JIT runner a
                // different job than the one it was minted for, and the webhook processor
                // releases the stale claim when it does, so an unbound or rebound instance
                // may be mid-build on another job. Reaping it here would kill that job,
                // which is the failure the completion path already learned to avoid.
                if (binding != LinkedJobBinding.Same)
                {
                    // Deliberately not a return: we decline this job-specific stop, but the
                    // instance must still face the max-runtime and stale-health backstops
                    // below. Terminal events are retained for days, so returning here would
                    // exempt the runner from every other cleanup path for that long.
                    _logger.LogInformation(
                        "Dynamic runner {RunnerName} ({Id}) is not bound to resolved event job {EventJobId} (claim: {JobId}); leaving it running",
                        instance.RunnerName, instance.Id, linkedEvent?.JobId, instance.JobId ?? "released");
                }
                else if (await GetProviderActivityAsync(registrations, store, instance, ct) is var resolvedActivity
                    && !ProviderAllowsRecycle(resolvedActivity))
                {
                    // A matching claim is RunnerRunner's belief, and it goes stale whenever the
                    // webhook that would release it is lost. The provider knows whether the
                    // runner is building; if it is, or cannot be asked, the stop waits.
                    _logger.LogInformation(
                        "Dynamic runner {RunnerName} ({Id}) is bound to resolved event job {EventJobId}, but the provider reports it {Activity}; leaving it running",
                        instance.RunnerName, instance.Id, linkedEvent?.JobId, resolvedActivity);
                }
                else
                {
                    _logger.LogWarning(
                        "Dynamic runner {RunnerName} ({Id}) is still active after its event resolved with status {EventStatus}",
                        instance.RunnerName, instance.Id, eventStatus);

                    if (!await TrySendStopRunner(store, instance))
                        return false;

                    instance.Status = RunnerInstanceStatus.Failed;
                    instance.StatusMessage = $"Dynamic runner cleanup after event resolved: {eventStatus}";
                    await store.Update(instance);
                    await store.Remove<RunnerInstance>(instance.Id);
                    return true;
                }
            }
            else if (instance.StartedAt != null
                && now - instance.StartedAt.Value > DynamicPickupTimeout
                && !string.Equals(eventStatus, "in_progress", StringComparison.OrdinalIgnoreCase))
            {
                // The linked event describes the job this runner was minted for, which is
                // not necessarily the job GitHub gave it. Recycling is only safe when the
                // runner still holds that exact claim; if it was rebound to another job, or
                // its claim was released because another runner took this one, the runner
                // may be building and must not be stopped.
                if (binding != LinkedJobBinding.Same)
                {
                    _logger.LogInformation(
                        "Dynamic runner {RunnerName} ({Id}) did not pick up job {EventJobId} and no longer holds that claim (now {JobId}); leaving it running",
                        instance.RunnerName, instance.Id, linkedEvent?.JobId, instance.JobId ?? "released");
                }
                else if (await GetProviderActivityAsync(registrations, store, instance, ct) is var pickupActivity
                    && !ProviderAllowsRecycle(pickupActivity))
                {
                    // Without webhooks the claim never moves, so "never picked up" really means
                    // "RunnerRunner never heard". GitHub hands a waiting JIT runner any job its
                    // labels match; stopping it here killed those jobs mid-step.
                    _logger.LogInformation(
                        "Dynamic runner {RunnerName} ({Id}) has not been seen picking up job {EventJobId}, but the provider reports it {Activity}; leaving it running",
                        instance.RunnerName, instance.Id, linkedEvent?.JobId, pickupActivity);
                }
                else
                {
                    _logger.LogWarning(
                        "Dynamic runner {RunnerName} ({Id}) never picked up its queued job after {Elapsed}; recycling it",
                        instance.RunnerName, instance.Id, now - instance.StartedAt.Value);

                    if (!await TrySendStopRunner(store, instance))
                        return false;

                    instance.Status = RunnerInstanceStatus.Failed;
                    instance.StatusMessage = "Dynamic runner pickup timeout — job never started on provider";
                    await store.Update(instance);

                    await TryRequeueLinkedWebhookEvent(
                        store,
                        instance,
                        now,
                        "Runner came online but never picked up the queued job; provisioning will be retried");
                    await store.Remove<RunnerInstance>(instance.Id);
                    return true;
                }
            }
        }

        // Dynamic runner running too long without completion webhook
        if (instance.ProvisioningMode == "dynamic"
            && instance.StartedAt != null
            && now - instance.StartedAt.Value > _dynamicRunningTimeout)
        {
            var activity = await GetProviderActivityAsync(registrations, store, instance, ct);
            if (activity == ProviderRunnerActivity.Busy)
            {
                _logger.LogWarning(
                    "Dynamic runner {RunnerName} ({Id}) passed the {Timeout} backstop, but the provider reports it is still running a job; leaving it running",
                    instance.RunnerName, instance.Id, _dynamicRunningTimeout);
                return false;
            }

            if (activity == ProviderRunnerActivity.Unknown
                && !IsPastUnknownActivityCeiling(instance.StartedAt.Value, now))
            {
                _logger.LogWarning(
                    "Dynamic runner {RunnerName} ({Id}) passed the {Timeout} backstop, but the provider cannot say whether it is running a job; leaving it running until {Ceiling} have passed",
                    instance.RunnerName, instance.Id, _dynamicRunningTimeout, UnknownActivityCeiling);
                return false;
            }

            _logger.LogWarning(
                "Dynamic runner {RunnerName} ({Id}) timed out after {Elapsed} with no completion webhook (provider: {Activity})",
                instance.RunnerName, instance.Id, now - instance.StartedAt.Value, activity);

            if (!await TrySendStopRunner(store, instance))
                return false;

            instance.Status = RunnerInstanceStatus.Failed;
            instance.StatusMessage = "Dynamic runner timeout — no completion webhook received";
            await store.Update(instance);

            await TryRequeueLinkedWebhookEvent(
                store,
                instance,
                now,
                "Runner exceeded the dynamic execution timeout before the job completed; provisioning will be retried if the provider still shows it queued");
            await store.Remove<RunnerInstance>(instance.Id);
            return true;
        }

        // Stale health means the server stopped hearing from the host (it slept, or the link
        // dropped), not that the runner exited: the VM or process keeps building meanwhile.
        // Only the host reporting the runner gone, or the provider reporting it idle or
        // deregistered (the recycle paths above), may end it.
        var lastSeenAt = instance.LastHealthCheck ?? instance.StartedAt;
        if (lastSeenAt != null
            && now - lastSeenAt.Value > HealthCheckStaleTimeout)
        {
            var message = $"{RunnerInstanceGrain.HealthStaleMessagePrefix} since {lastSeenAt.Value:u} — awaiting host confirmation";
            if (!string.Equals(instance.StatusMessage, message, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Runner {RunnerName} ({Id}) has stale health check (last seen: {LastHealth}); leaving it running until the host confirms it exited",
                    instance.RunnerName, instance.Id, lastSeenAt.Value);

                instance.StatusMessage = message;
                await store.Update(instance);
            }
        }

        return false;
    }

    private async Task<ProviderRunnerActivity> GetProviderActivityAsync(
        RunnerRegistrationCleanupService? registrations,
        IDocumentStore store,
        RunnerInstance instance,
        CancellationToken ct)
    {
        if (registrations == null)
            return ProviderRunnerActivity.NotSupported;

        var now = DateTime.UtcNow;
        if (_blockingActivity.TryGetValue(instance.Id, out var cached)
            && now - cached.CheckedAt < ProviderActivityCacheDuration)
        {
            return cached.Activity;
        }

        ProviderRunnerActivity activity;
        try
        {
            activity = await registrations.GetProviderRunnerActivityAsync(store, instance, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not determine provider activity for runner {RunnerName}", instance.RunnerName);
            activity = ProviderRunnerActivity.Unknown;
        }

        if (activity is ProviderRunnerActivity.Busy or ProviderRunnerActivity.Unknown)
            _blockingActivity[instance.Id] = (activity, now);
        else
            _blockingActivity.TryRemove(instance.Id, out _);

        return activity;
    }

    /// <summary>
    /// A job-specific recycle stops a runner RunnerRunner believes is idle. It may only do so
    /// when the provider agrees, or when the provider cannot be asked about single runners.
    /// </summary>
    internal static bool ProviderAllowsRecycle(ProviderRunnerActivity activity) =>
        activity is ProviderRunnerActivity.Idle
            or ProviderRunnerActivity.NotRegistered
            or ProviderRunnerActivity.NotSupported;

    /// <summary>
    /// How long a runner the provider cannot answer about (Unknown) is left alone; shared with
    /// <see cref="ReconciliationService"/>'s orphan cleanup.
    /// </summary>
    private TimeSpan UnknownActivityCeiling => DynamicRunnerTimeouts.GetUnknownActivityCeiling(_dynamicRunningTimeout);

    private bool IsPastUnknownActivityCeiling(DateTime startedAt, DateTime now) =>
        now - startedAt > UnknownActivityCeiling;

    private async Task CheckStoppingTimeout(IDocumentStore store, RunnerInstance instance, DateTime now)
    {
        // Use StoppedAt as a proxy for when Stopping was set; fall back to StartedAt/DeployedAt/CreatedAt
        var referenceTime = instance.StoppedAt ?? instance.StartedAt ?? instance.DeployedAt ?? instance.CreatedAt;
        if (now - referenceTime <= StoppingTimeout)
            return;

        _logger.LogWarning(
            "Runner {RunnerName} ({Id}) timed out in Stopping state after {Elapsed}",
            instance.RunnerName, instance.Id, now - referenceTime);

        instance.Status = RunnerInstanceStatus.Failed;
        instance.StatusMessage = "Stop timeout";
        await store.Update(instance);
    }

    /// <summary>
    /// Sends a StopRunner command to the HostWorker hosting this instance.
    /// </summary>
    /// <returns>
    /// False when the command could not be sent (the host is not connected; there is no
    /// outbox), or there is no host record to send it to. The caller must then keep the
    /// record: forgetting it leaves the runner registered and running with no record, and
    /// reconciliation later cleans it up as an orphan -- by then possibly mid-way through a
    /// job GitHub gave it. True means the command was handed to the host's stream, not that
    /// the host stopped the runner.
    /// </returns>
    private async Task<bool> TrySendStopRunner(IDocumentStore store, RunnerInstance instance)
    {
        try
        {
            var host = (await store.Query<Host>().ToList()).FirstOrDefault(h => h.Id == instance.HostId);
            if (host == null)
            {
                _logger.LogWarning("No host record {HostId} for instance {RunnerName} ({Id}); cannot send StopRunner, keeping its record", instance.HostId, instance.RunnerName, instance.Id);
                return false;
            }

            await _hostCommands.DispatchStopRunnerAsync(host.Id, StopRunnerCommand.ForInstance(instance.Id, instance.ContainerId, instance.VmName, instance.ProcessId));

            _logger.LogInformation("Sent StopRunner for timed-out instance {RunnerName} ({Id})",
                instance.RunnerName, instance.Id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send StopRunner for instance {RunnerName} ({Id}); keeping its record to retry", instance.RunnerName, instance.Id);
            return false;
        }
    }

    private async Task TryRequeueLinkedWebhookEvent(
        IDocumentStore store,
        RunnerInstance instance,
        DateTime now,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(instance.WebhookEventId))
            return;

        var linkedEvent = await store.Get<WebhookEvent>(instance.WebhookEventId);
        if (!PrepareLinkedEventRetry(linkedEvent, now, reason))
            return;

        await store.Update(linkedEvent!);
        _logger.LogInformation(
            "Re-queued webhook event {EventId} after runner failure for instance {InstanceId}",
            linkedEvent!.Id,
            instance.Id);
    }

    /// <summary>
    /// How an instance's current job claim relates to the job its linked webhook event
    /// describes. JIT runners are not pinned to the job they were minted for, so this
    /// relationship is the only signal the timeout paths have about what a runner is
    /// actually doing — and it is frequently unknowable.
    /// </summary>
    internal enum LinkedJobBinding
    {
        /// <summary>The instance still claims exactly the job the linked event describes.</summary>
        Same,

        /// <summary>GitHub handed this runner a different job; it is building something else.</summary>
        Different,

        /// <summary>
        /// There is no usable claim to compare. This is NOT the same as idle: the webhook
        /// processor clears an instance's claim once another runner takes its job, so an
        /// unbound runner may be mid-build on work we cannot name.
        /// </summary>
        Unknown
    }

    /// <summary>
    /// Classifies an instance against its linked event. Only <see cref="LinkedJobBinding.Same"/>
    /// is positive evidence; every job-specific stop must require it, because treating
    /// <see cref="LinkedJobBinding.Unknown"/> as idle is what kills live jobs.
    /// </summary>
    internal static LinkedJobBinding ClassifyLinkedJob(RunnerInstance instance, WebhookEvent? linkedEvent)
    {
        if (linkedEvent == null)
            return LinkedJobBinding.Unknown;

        // Authoritative: the provider named the runner it actually gave this job to. This
        // also closes a race the job claim cannot -- an instance may still hold a stale
        // claim on this job while already building a different one, simply because that
        // job's in_progress webhook has not been delivered yet.
        if (!string.IsNullOrWhiteSpace(linkedEvent.AssignedRunnerName))
        {
            return string.Equals(instance.RunnerName, linkedEvent.AssignedRunnerName, StringComparison.OrdinalIgnoreCase)
                ? LinkedJobBinding.Same
                : LinkedJobBinding.Different;
        }

        // Fallback for events resolved before any assignment was observed.
        if (string.IsNullOrWhiteSpace(instance.JobId)
            || string.IsNullOrWhiteSpace(linkedEvent.JobId))
        {
            return LinkedJobBinding.Unknown;
        }

        return string.Equals(instance.JobId, linkedEvent.JobId, StringComparison.OrdinalIgnoreCase)
            ? LinkedJobBinding.Same
            : LinkedJobBinding.Different;
    }

    internal static bool PrepareLinkedEventRetry(WebhookEvent? linkedEvent, DateTime now, string reason)
    {
        if (linkedEvent == null
            || !string.Equals(linkedEvent.Action, "queued", StringComparison.OrdinalIgnoreCase)
            || linkedEvent.IsTerminal)
            return false;

        if (linkedEvent.RetryCount >= MaxConsecutiveRunnerFailureRetries)
        {
            linkedEvent.Status = "timed_out";
            linkedEvent.Error = $"Runner failed to start the queued job {MaxConsecutiveRunnerFailureRetries} times in a row: {reason}";
            linkedEvent.ResolvedAt = now;
            linkedEvent.UpdatedAt = now;
            linkedEvent.NextRetryAt = null;
            linkedEvent.ExpiresAt = null;
            return true;
        }

        linkedEvent.ResolvedAt = null;
        linkedEvent.InstanceId = null;
        linkedEvent.ScheduleRetry(
            reason,
            now,
            RetryAfterRunnerFailureDelay,
            status: "pending",
            countAttempt: true);
        return true;
    }

    private async Task CleanupExpiredDynamicRecord(
        IDocumentStore store,
        RunnerRegistrationCleanupService? registrations,
        RunnerInstance instance,
        DateTime now,
        CancellationToken ct)
    {
        if (instance.ProvisioningMode != "dynamic"
            || instance.Status is not (RunnerInstanceStatus.Stopped or RunnerInstanceStatus.Failed or RunnerInstanceStatus.Crashed))
        {
            return;
        }

        var terminalAt = instance.StoppedAt ?? instance.LastHealthCheck ?? instance.StartedAt ?? instance.DeployedAt ?? instance.CreatedAt;
        if (HostReportedGone(instance))
        {
            if (now - terminalAt > DynamicTerminalRetention)
                await store.Remove<RunnerInstance>(instance.Id);
            return;
        }

        // Either the host's reports keep listing this runner's VM/container/process as running,
        // or the server has not heard from the host since (stale health). Neither says the
        // resource is gone: forgetting the record would turn it into an "orphan" and have it
        // cleaned up. Keep the record until the provider confirms the runner is not working,
        // then stop it by handle.
        var terminalSince = GetTerminalSince(instance);
        if (now - (terminalSince ?? terminalAt) <= DynamicTerminalRetention)
            return;

        var hostReportsIt = HostStillReportsActive(instance, terminalSince, now);

        var activity = await GetProviderActivityAsync(registrations, store, instance, ct);
        var startedAt = instance.StartedAt ?? instance.DeployedAt ?? instance.CreatedAt;
        if (!ProviderAllowsRecycle(activity)
            && !(activity == ProviderRunnerActivity.Unknown && IsPastUnknownActivityCeiling(startedAt, now)))
        {
            _logger.LogInformation(
                "Keeping {Status} record for runner {RunnerName} ({Id}): the host {HostView} and the provider reports it {Activity}",
                instance.Status, instance.RunnerName, instance.Id,
                hostReportsIt ? "still reports it running" : "has not reported it gone", activity);
            return;
        }

        _logger.LogWarning(
            "Runner {RunnerName} ({Id}) is {Status}, the host {HostView}, and the provider reports it {Activity}; stopping it",
            instance.RunnerName, instance.Id, instance.Status,
            hostReportsIt ? "still reports it running" : "has not reported it gone", activity);
        if (await TrySendStopRunner(store, instance))
            await store.Remove<RunnerInstance>(instance.Id);
    }

    /// <summary>
    /// True when the host affirmatively reported this runner's resource gone (see
    /// <see cref="RunnerInstance.HostReportedGoneAt"/>) and has not reported it running since.
    /// </summary>
    internal static bool HostReportedGone(RunnerInstance instance) =>
        instance.HostReportedGoneAt is { } gone
        && (instance.LastHealthCheck is not { } seen || seen < gone);

    private static DateTime? GetTerminalSince(RunnerInstance instance) =>
        instance.StoppedAt
        ?? instance.StatusHistory?.LastOrDefault(e => e.Status == instance.Status)?.Timestamp;

    /// <summary>
    /// Reconciliation refreshes <see cref="RunnerInstance.LastHealthCheck"/> for every record
    /// whose resource the host reports active, whatever the record's status. A refresh after
    /// the record went terminal means the host still runs it.
    /// </summary>
    private static bool HostStillReportsActive(RunnerInstance instance, DateTime? terminalSince, DateTime now) =>
        instance.LastHealthCheck is { } seen
        && now - seen <= HealthCheckStaleTimeout
        && (terminalSince is null || seen > terminalSince.Value);

    private async Task CleanupExpiredWebhookEvents(IDocumentStore store, DateTime now, CancellationToken ct)
    {
        var webhookEvents = (await store.Query<WebhookEvent>().ToList()).ToList();
        var removed = 0;

        foreach (var evt in webhookEvents)
        {
            ct.ThrowIfCancellationRequested();

            if (!ShouldRemoveWebhookEvent(evt, now))
                continue;

            await store.Remove<WebhookEvent>(evt.Id);
            removed++;
        }

        if (removed > 0)
            _logger.LogInformation("Cleaned up {Count} expired webhook event records", removed);
    }

    private static bool ShouldRemoveWebhookEvent(WebhookEvent evt, DateTime now)
    {
        var age = now - evt.ReceivedAt;

        if (evt.Status is "no_match" or "pending_host_match" or "pending_config")
            return age > StaleNoMatchRetention;

        if (evt.Status is "completed" or "timed_out")
            return age > CompletedWebhookRetention;

        if (evt.Status is "ignored" or WebhookEvent.StatusIgnoredScope or WebhookEvent.StatusIgnoredTarget or "rejected")
            return age > IgnoredWebhookRetention;

        if (evt.Status is "in_progress" or "provisioned")
            return age > CompletedWebhookRetention;

        return false;
    }
}
