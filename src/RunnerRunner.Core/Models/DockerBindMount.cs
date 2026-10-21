using Orleans;

namespace RunnerRunner.Core.Models;

[GenerateSerializer]
public sealed class DockerBindMount
{
    public const string RequiredHostCapability = "docker-verified-binds";
    [Id(0)]
    public string GrantId { get; set; } = "";
    [Id(1)]
    public string ContainerPath { get; set; } = "";
    [Id(2)]
    public bool ReadOnly { get; set; } = true;

    public DockerBindMount Clone() => new() { GrantId = GrantId, ContainerPath = ContainerPath, ReadOnly = ReadOnly };

    public static bool IsCanonicalPath(string? path) =>
        !string.IsNullOrEmpty(path) && path.Length <= 4096 && path.StartsWith('/') && path != "/"
        && path.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.')
        && path.Split('/').Skip(1).All(p => p.Length > 0 && p is not "." and not "..");

    public static bool Overlaps(string left, string right) =>
        left == right || left.StartsWith(right + "/", StringComparison.Ordinal)
        || right.StartsWith(left + "/", StringComparison.Ordinal);

    public static bool IsProtectedPath(string path)
    {
        string[] roots = ["/bin", "/boot", "/dev", "/etc", "/lib", "/lib32", "/lib64", "/proc",
            "/run", "/sbin", "/sys", "/usr", "/var", "/root", "/home", "/runnerrunner"];
        return roots.Any(root => Overlaps(path, root))
            || path.Split('/').Any(p => p.EndsWith(".sock", StringComparison.OrdinalIgnoreCase))
            || new[] { "appdata", "games" }.Contains(path.Split('/').Last(), StringComparer.OrdinalIgnoreCase);
    }

    public static string? GetValidationError(IReadOnlyList<DockerBindMount> mounts, DockerScratchConfig? scratch = null)
    {
        if (mounts.Count > 32)
            return "At most 32 Docker bind mounts are allowed.";
        for (var i = 0; i < mounts.Count; i++)
        {
            var mount = mounts[i];
            if (string.IsNullOrWhiteSpace(mount.GrantId) || mount.GrantId.Length > 128
                || !mount.GrantId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
                return "Docker bind grant IDs must use letters, digits, -, _, or .";
            if (!IsCanonicalPath(mount.ContainerPath) || IsProtectedPath(mount.ContainerPath)
                || Overlaps(mount.ContainerPath, "/tmp")
                || (scratch != null && Overlaps(mount.ContainerPath, scratch.WorkDirectory)))
                return "Docker bind destinations must be canonical absolute Linux paths outside system, scratch, and control-plane directories.";
            if (mounts.Take(i).Any(other => other.GrantId == mount.GrantId || Overlaps(other.ContainerPath, mount.ContainerPath)))
                return "Docker bind grants and destinations must be unique and non-overlapping.";
        }
        return null;
    }
}
