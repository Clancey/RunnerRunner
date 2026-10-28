using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Agent.Backends;

/// <summary>
/// Host-administrator-owned Docker settings, bound from <c>HostWorker:Docker</c>.
/// Nothing here can be supplied by a rule, a workflow or a job; the host decides
/// which host directories a runner definition may see.
/// </summary>
public sealed class DockerHostOptions
{
    public List<DockerBindGrant> BindGrants { get; set; } = [];

    /// <summary>
    /// Bind mounts for one start request: every grant whose <see cref="DockerBindGrant.ProfileId"/>
    /// equals the request's profile (runner definition) id. Throws when a matching grant is unusable,
    /// so a misconfigured host fails the start instead of silently running on the container root.
    /// </summary>
    internal List<ResolvedBindMount> Resolve(RunnerStartRequest request)
    {
        var result = new List<ResolvedBindMount>();
        if (BindGrants.Count == 0 || string.IsNullOrWhiteSpace(request.ProfileId))
            return result;

        foreach (var grant in BindGrants)
        {
            if (!string.Equals(grant.ProfileId, request.ProfileId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (grant.GetValidationError() is { } error)
                throw new InvalidOperationException($"Docker bind grant '{grant.Id}': {error}");

            var source = grant.Source;
            if (grant.PerRunner)
            {
                if (!DockerBindGrant.IsSafeDirectoryName(request.RunnerName))
                    throw new InvalidOperationException($"Docker bind grant '{grant.Id}': runner name '{request.RunnerName}' is not a safe directory name.");
                source = $"{grant.Source}/{request.RunnerName}";
            }

            if (result.Any(existing => DockerBindGrant.Overlaps(existing.Target, grant.Target)))
                throw new InvalidOperationException($"Docker bind grant '{grant.Id}': target '{grant.Target}' overlaps another grant for the same profile.");

            result.Add(new ResolvedBindMount(grant, source));
        }

        return result;
    }
}

/// <summary>One host directory a runner definition is allowed to mount.</summary>
public sealed class DockerBindGrant
{
    /// <summary>Administrative name, used in logs and errors.</summary>
    public string Id { get; set; } = "";

    /// <summary>Runner definition (profile) id the grant applies to. Required.</summary>
    public string ProfileId { get; set; } = "";

    /// <summary>Absolute host directory, as the Docker daemon sees it.</summary>
    public string Source { get; set; } = "";

    /// <summary>Absolute container path.</summary>
    public string Target { get; set; } = "";

    public bool ReadOnly { get; set; }

    /// <summary>
    /// When true, each runner gets its own empty subdirectory <c>Source/&lt;runner name&gt;</c>, created
    /// before the container starts and deleted when the container is removed. The HostWorker must see
    /// <see cref="Source"/> at the same path as the daemon (bind it into a containerized worker).
    /// </summary>
    public bool PerRunner { get; set; }

    private static readonly string[] ProtectedTargets =
    [
        "/bin", "/boot", "/dev", "/etc", "/lib", "/lib32", "/lib64", "/proc", "/run",
        "/sbin", "/sys", "/tmp", "/usr", "/var/run", "/var/lib", "/var/log", "/var/tmp", "/runnerrunner"
    ];

    private static readonly string[] ProtectedSources =
    [
        "/bin", "/boot", "/dev", "/etc", "/lib", "/lib32", "/lib64", "/proc", "/run",
        "/sbin", "/sys", "/usr", "/var/lib/docker", "/var/run", "/root", "/home"
    ];

    public string? GetValidationError()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 128 || !Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            return "Id must use letters, digits, -, _ or .";
        if (string.IsNullOrWhiteSpace(ProfileId))
            return "ProfileId is required.";
        if (!IsCanonicalPath(Source))
            return "Source must be a canonical absolute Linux path.";
        if (Source.Split('/').Length < 3)
            return "Source must be at least two levels below the host root.";
        if (ProtectedSources.Any(root => Overlaps(Source, root)) || Source.Split('/').Any(part => part.EndsWith(".sock", StringComparison.OrdinalIgnoreCase)))
            return "Source cannot be a system, Docker, home or socket path.";
        if (!IsCanonicalPath(Target))
            return "Target must be a canonical absolute Linux path.";
        if (ProtectedTargets.Any(root => Overlaps(Target, root)))
            return "Target cannot be a system path, /tmp or the RunnerRunner hook directory.";
        if (PerRunner && ReadOnly)
            return "A per-runner directory cannot be read-only.";
        return null;
    }

    public static bool IsCanonicalPath(string? path) =>
        !string.IsNullOrEmpty(path) && path.Length <= 4096 && path.StartsWith('/') && path != "/"
        && path.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.')
        && path.Split('/').Skip(1).All(part => part.Length > 0 && part is not "." and not "..");

    public static bool IsSafeDirectoryName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 255 && name is not "." and not ".."
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    public static bool Overlaps(string left, string right) =>
        left == right
        || left.StartsWith(right + "/", StringComparison.Ordinal)
        || right.StartsWith(left + "/", StringComparison.Ordinal);
}

internal sealed record ResolvedBindMount(DockerBindGrant Grant, string Source)
{
    public string Target => Grant.Target;
    public bool ReadOnly => Grant.ReadOnly;
    public bool PerRunner => Grant.PerRunner;
}
