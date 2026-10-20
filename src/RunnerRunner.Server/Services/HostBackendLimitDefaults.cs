using RunnerRunner.Core.Models;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Services;

/// <summary>
/// Initial backend limits for newly enrolled hosts. Existing limits are managed
/// through the Hosts UI, not reapplied when capabilities or defaults change.
/// </summary>
internal static class HostBackendLimitDefaults
{
    public const int DefaultMaxDockerContainers = 10;
    public const int DefaultMaxTartVMs = 3;
    public const int DefaultMaxNativeProcesses = 5;

    public static void ApplyToNewHost(Host host)
    {
        var capabilities = host.Capabilities.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var isMac = host.Platform == HostPlatform.MacOS;
        var hasTart = isMac && capabilities.Contains("tart");
        var hasDocker = !isMac && capabilities.Contains("docker");

        host.MaxTartVMs = hasTart ? DefaultMaxTartVMs : 0;
        host.MaxDockerContainers = hasDocker ? DefaultMaxDockerContainers : 0;
        host.MaxNativeProcesses = !hasTart && !hasDocker ? DefaultMaxNativeProcesses : 0;
    }
}
