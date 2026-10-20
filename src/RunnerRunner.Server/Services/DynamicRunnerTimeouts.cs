namespace RunnerRunner.Server.Services;

/// <summary>
/// Dynamic-runner timeouts that <see cref="RunnerTimeoutService"/> and
/// <see cref="ReconciliationService"/> must agree on, read from the same configuration.
/// </summary>
internal static class DynamicRunnerTimeouts
{
    internal const string RunningTimeoutMinutesKey = "DynamicProvisioning:RunningTimeoutMinutes";

    private static readonly TimeSpan DefaultRunningTimeout = TimeSpan.FromHours(6);

    /// <summary>
    /// Backstop for dynamic runners whose completion webhook was lost. It must stay above
    /// the longest job the pool can legitimately run: a runner that trips this is killed
    /// and its workspace deleted, so a value below a real job's timeout destroys healthy
    /// builds mid-step and reports it as a build failure. Runners that really exited are
    /// caught far sooner by the host reporting them gone, so this only needs to be generous.
    /// </summary>
    internal static TimeSpan GetRunningTimeout(IConfiguration configuration)
    {
        var configuredMinutes = configuration.GetValue(
            RunningTimeoutMinutesKey,
            (int)DefaultRunningTimeout.TotalMinutes);

        return TimeSpan.FromMinutes(Math.Max(1, configuredMinutes));
    }

    /// <summary>
    /// How long a runner the provider cannot answer about (Unknown) is left alone, tracked
    /// or not. Busy is never stopped; Unknown is usually a GitHub outage or a credential
    /// problem, so it waits well past any job the running timeout allows before giving up.
    /// </summary>
    internal static TimeSpan GetUnknownActivityCeiling(IConfiguration configuration) =>
        GetUnknownActivityCeiling(GetRunningTimeout(configuration));

    internal static TimeSpan GetUnknownActivityCeiling(TimeSpan runningTimeout) => runningTimeout * 2;
}
