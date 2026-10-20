using Orleans;

namespace RunnerRunner.Core.Models;

/// <summary>Opt-in, empty, per-container RAM scratch for Linux one-job GitHub JIT runners.</summary>
[GenerateSerializer]
public class DockerScratchConfig
{
    public const int MaxSizeMb = 1_048_576;

    [Id(0)]
    public string WorkDirectory { get; set; } = "/actions-runner/_work";
    [Id(1)]
    public int WorkSizeMb { get; set; } = 4096;
    [Id(2)]
    public int TempSizeMb { get; set; } = 1024;

    public DockerScratchConfig Clone() => new()
    {
        WorkDirectory = WorkDirectory,
        WorkSizeMb = WorkSizeMb,
        TempSizeMb = TempSizeMb
    };

    public string? GetValidationError()
    {
        // Use Linux syntax, irrespective of the OS hosting the server. A restricted
        // alphabet also keeps mount destinations out of shell and mount-option syntax.
        if (string.IsNullOrEmpty(WorkDirectory)
            || WorkDirectory.Length > 4096
            || !WorkDirectory.StartsWith('/')
            || !WorkDirectory.EndsWith("/_work", StringComparison.Ordinal)
            || WorkDirectory == "/_work"
            || WorkDirectory.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.'))
            || WorkDirectory.Split('/').Skip(1).Any(part => part.Length == 0 || part is "." or ".."))
        {
            return "Docker scratch work directory must be a canonical absolute Linux path ending in /_work, using only letters, digits, /, -, _, and . (no root, trailing slash, or dot segments).";
        }

        string[] protectedPaths =
        [
            "/bin", "/boot", "/dev", "/etc", "/lib", "/lib32", "/lib64", "/proc", "/run",
            "/sbin", "/sys", "/tmp", "/usr", "/var/run", "/var/lib", "/var/log", "/var/tmp",
            "/opt/hostedtoolcache", "/root/.nuget", "/root/.npm"
        ];
        if (protectedPaths.Any(path => WorkDirectory.StartsWith(path + "/", StringComparison.Ordinal)))
            return "Docker scratch work directory cannot be under /tmp, system directories, or package/tool caches.";

        if (WorkSizeMb is < 1 or > MaxSizeMb || TempSizeMb is < 1 or > MaxSizeMb)
            return $"Docker scratch sizes must each be between 1 and {MaxSizeMb} MiB.";

        return null;
    }

    /// <summary>
    /// Editor-side check. <paramref name="jitDispatch"/> is false for rule types that never
    /// provide a JIT config (static and scale-set rules).
    /// </summary>
    public string? GetValidationError(HostPlatform platform, bool ephemeral, RunnerProvider provider, bool jitDispatch)
    {
        if (platform != HostPlatform.Linux || !jitDispatch)
            return "Docker scratch requires Linux, one-job (ephemeral) GitHub JIT runners.";

        return GetRunnerValidationError(ephemeral, provider);
    }

    /// <summary>
    /// Backend check of the runner shape. The container OS is checked separately from the image,
    /// because the worker process OS says nothing about the Docker daemon's container OS.
    /// </summary>
    public string? GetRunnerValidationError(bool ephemeral, RunnerProvider provider)
    {
        if (!ephemeral || provider != RunnerProvider.GitHubActions)
            return "Docker scratch requires Linux, one-job (ephemeral) GitHub JIT runners.";

        return GetValidationError();
    }
}
