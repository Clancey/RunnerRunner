using System.Collections.Concurrent;

namespace RunnerRunner.Agent.Services;

/// <summary>
/// Recursively deletes runner-controlled directories without ever deleting through a mount point.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Directory.Delete(string, bool)"/> (like plain <c>rm -rf</c>) walks into anything
/// mounted beneath the directory. A CI job that mounts a network share inside its workspace and
/// is then cancelled would have that share's contents wiped by instance cleanup.
/// </para>
/// <para>
/// Before deleting, every mount at or beneath the directory is unmounted (deepest first, never
/// forced). Anything still mounted afterwards is left in place along with its ancestors, and
/// everything else is deleted. If the mount table can't be read, nothing is deleted.
/// </para>
/// <para>
/// The walk goes through open directory handles (openat with O_NOFOLLOW, unlinkat), so nothing
/// swapped in mid-walk can redirect it. As a backstop it compares every directory's st_dev (and,
/// on Linux, mount id) with the root's and never descends into a different filesystem, even one
/// the mount table didn't list. Symlinks are removed, never followed.
/// </para>
/// </remarks>
public sealed class MountSafeDirectoryDeleter
{
    public static readonly TimeSpan DefaultUnmountTimeout = TimeSpan.FromSeconds(30);

    // A mount that just refused to unmount (or timed out on a dead server) is not retried for a
    // while, so one cleanup pass doesn't block on the same mount once per directory it checks.
    private static readonly TimeSpan UnmountRetryDelay = TimeSpan.FromMinutes(2);

    // macOS can report Data-volume paths through the firmlink (/System/Volumes/Data/Users/...).
    private const string MacDataVolumePrefix = "/System/Volumes/Data/";

    private readonly ILogger _logger;
    private readonly IMountSafeFileSystem _fileSystem;
    private readonly TimeSpan _unmountTimeout;
    private readonly StringComparison _pathComparison;
    private readonly StringComparer _pathComparer;
    private readonly ConcurrentDictionary<string, DateTime> _recentUnmountFailures;
    private readonly bool _runningAsRoot;

    // macOS volumes are usually case-insensitive and callers' paths may differ in case from the
    // mount table, which would hide a live mount from us. Over-matching instead can at worst
    // attempt a normal (never forced) unmount of a case-variant path on a case-sensitive volume,
    // and makes the walk skip more; it can never cause a delete.
    public MountSafeDirectoryDeleter(ILogger logger)
        : this(logger, SystemMountSafeFileSystem.Instance, caseInsensitivePaths: OperatingSystem.IsMacOS())
    {
    }

    public MountSafeDirectoryDeleter(
        ILogger logger,
        IMountSafeFileSystem fileSystem,
        TimeSpan? unmountTimeout = null,
        bool caseInsensitivePaths = false,
        bool? runningAsRoot = null)
    {
        _runningAsRoot = runningAsRoot ?? Environment.IsPrivilegedProcess;
        _logger = logger;
        _fileSystem = fileSystem;
        _unmountTimeout = unmountTimeout ?? DefaultUnmountTimeout;
        _pathComparison = caseInsensitivePaths ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        _pathComparer = caseInsensitivePaths ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        _recentUnmountFailures = new ConcurrentDictionary<string, DateTime>(_pathComparer);
    }

    /// <summary>
    /// Unmounts every mount at or beneath <paramref name="path"/>, deepest first.
    /// </summary>
    /// <returns>
    /// The mount points still mounted afterwards, or null when that can't be determined (the
    /// path can't be resolved or the mount table can't be read), which callers must treat as unsafe.
    /// </returns>
    public IReadOnlyList<string>? UnmountBeneath(string path)
    {
        if (!_fileSystem.SupportsMountDetection)
            return [];

        try
        {
            if (_fileSystem.GetEntryKind(path) != FileSystemEntryKind.Directory)
                return [];

            var root = CanonicalizeKeepingLastComponent(path);
            return root is null ? null : UnmountBeneathCanonical(root)?.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not check {Path} for mounts", path);
            return null;
        }
    }

    /// <summary>Deletes <paramref name="path"/> and throws if anything had to be left behind.</summary>
    public void DeleteOrThrow(string path)
    {
        var result = Delete(path);
        if (!result.Completed)
            throw new IOException($"Could not fully delete '{path}': {result.Describe()}");
    }

    public MountSafeDeleteResult Delete(string path)
    {
        var result = new MountSafeDeleteResult(path);

        FileSystemEntryKind kind;
        try
        {
            kind = _fileSystem.GetEntryKind(path);
        }
        catch (Exception ex)
        {
            result.AddFailure(path, ex.Message);
            LogFailures(result);
            return result;
        }

        if (kind == FileSystemEntryKind.Missing)
        {
            result.Completed = true;
            return result;
        }

        if (kind != FileSystemEntryKind.Directory)
        {
            // A symlink is removed, never followed; a plain file can't contain a mount.
            TryRun(() => _fileSystem.DeleteFile(path), path, result);
            result.Completed = result.Failures.Count == 0;
            return result;
        }

        if (!_fileSystem.SupportsMountDetection)
        {
            // Windows: Directory.Delete removes junctions and volume mount points without
            // traversing them.
            TryRun(() => Directory.Delete(path, recursive: true), path, result);
            result.Completed = result.Failures.Count == 0;
            return result;
        }

        string? root;
        try
        {
            root = CanonicalizeKeepingLastComponent(path);
        }
        catch (Exception ex)
        {
            result.AddFailure(path, ex.Message);
            LogFailures(result);
            return result;
        }

        var parentPath = root is null ? null : GetParent(root);
        if (root is null || parentPath is null)
        {
            result.AddFailure(path, "refusing to delete the filesystem root");
            LogFailures(result);
            return result;
        }

        var remaining = UnmountBeneathCanonical(root);
        if (remaining is null)
        {
            _logger.LogError(
                "Refusing to delete {Path}: the mount table could not be read, so mounts beneath it can't be ruled out. Nothing was deleted",
                root);
            result.AddFailure(root, "mount table unavailable");
            return result;
        }

        result.RemainingMounts.AddRange(remaining.OrderBy(p => p, StringComparer.Ordinal));
        if (remaining.Contains(root))
            return result;

        var name = root[(parentPath.Length == 1 ? 1 : parentPath.Length + 1)..];
        try
        {
            using var parent = OpenWithoutFollowingSymlinks(parentPath);
            using var rootDirectory = parent?.OpenChildDirectory(name);
            if (parent is null)
            {
                result.AddFailure(root, "a directory above it was replaced by a symlink or removed while it was being deleted");
                LogFailures(result);
                return result;
            }

            if (rootDirectory is null)
            {
                result.AddFailure(root, "it stopped being a directory while it was being deleted");
                LogFailures(result);
                return result;
            }

            if (IsDifferentFilesystem(rootDirectory, parent))
            {
                _logger.LogError(
                    "Refusing to delete {Path}: it is itself a mount point (device {Device}, mount {MountId}; parent device {ParentDevice}, mount {ParentMountId}) that is not in the mount table. Nothing was deleted",
                    root, rootDirectory.DeviceId, rootDirectory.MountId, parent.DeviceId, parent.MountId);
                result.SkippedForeignDevices.Add(root);
                return result;
            }

            result.Completed = DeleteContents(rootDirectory, rootDirectory, remaining, result)
                && TryRun(() => parent.DeleteChildDirectory(name), root, result);
        }
        catch (Exception ex)
        {
            result.AddFailure(root, ex.Message);
        }

        LogFailures(result);
        return result;
    }

    private bool DeleteContents(
        IMountSafeDirectoryHandle directory,
        IMountSafeDirectoryHandle root,
        HashSet<string> remainingMounts,
        MountSafeDeleteResult result)
    {
        IReadOnlyList<string> names;
        try
        {
            names = directory.ReadEntryNames();
        }
        catch (Exception ex)
        {
            result.AddFailure(directory.Path, ex.Message);
            return false;
        }

        var emptied = true;
        foreach (var name in names)
        {
            var entryPath = directory.Path + "/" + name;

            // Never open a remaining mount point: a dead network share can block forever.
            if (remainingMounts.Contains(entryPath))
            {
                emptied = false;
                continue;
            }

            IMountSafeDirectoryHandle? child;
            try
            {
                child = directory.OpenChildDirectory(name);
            }
            catch (Exception ex)
            {
                result.AddFailure(entryPath, ex.Message);
                emptied = false;
                continue;
            }

            if (child is null)
            {
                // A file, a symlink (removed, never followed), or already gone.
                if (!TryRun(() => directory.DeleteChild(name), entryPath, result))
                    emptied = false;
                continue;
            }

            using (child)
            {
                if (IsDifferentFilesystem(child, root))
                {
                    _logger.LogError(
                        "Refusing to delete through {Path}: it is on a different filesystem (device {Device}, mount {MountId}) than {Root} (device {RootDevice}, mount {RootMountId}), so it is a mount the mount table did not list. Left it and its contents in place",
                        entryPath, child.DeviceId, child.MountId, root.Path, root.DeviceId, root.MountId);
                    result.SkippedForeignDevices.Add(entryPath);
                    emptied = false;
                }
                else if (!DeleteContents(child, root, remainingMounts, result)
                         || !TryRun(() => directory.DeleteChildDirectory(name), entryPath, result))
                {
                    emptied = false;
                }
            }
        }

        return emptied;
    }

    private static bool IsDifferentFilesystem(IMountSafeDirectoryHandle directory, IMountSafeDirectoryHandle reference)
        => directory.DeviceId != reference.DeviceId || directory.MountId != reference.MountId;

    /// <summary>
    /// Opens <paramref name="canonicalPath"/> one component at a time from "/" with O_NOFOLLOW,
    /// so an ancestor swapped for a symlink after it was resolved can't redirect the delete.
    /// Returns null when a component is no longer a real directory.
    /// </summary>
    private IMountSafeDirectoryHandle? OpenWithoutFollowingSymlinks(string canonicalPath)
    {
        var current = _fileSystem.OpenDirectory("/");
        try
        {
            foreach (var component in canonicalPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var next = current.OpenChildDirectory(component);
                current.Dispose();
                current = next!;
                if (next is null)
                    return null;
            }
            return current;
        }
        catch
        {
            current?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Resolves the ancestors of <paramref name="path"/> one component at a time, keeping its last
    /// component as given. Only symlinks directly in "/" are followed (e.g. /var -> /private/var,
    /// /bin -> usr/bin), since only root can create or replace those. Any other symlinked ancestor
    /// could have been created or swapped in by a job, so it is refused rather than letting it
    /// redirect the delete onto another tree such as a mounted share.
    /// Components that are real directories now but get swapped later are caught by the
    /// O_NOFOLLOW walk. Returns null for the filesystem root.
    /// </summary>
    private string? CanonicalizeKeepingLastComponent(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parent = GetParent(full);
        if (parent is null)
            return null;

        var name = full[(parent.Length == 1 ? 1 : parent.Length + 1)..];
        var resolved = "/";
        foreach (var component in parent.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = resolved == "/" ? "/" + component : resolved + "/" + component;
            if (_fileSystem.GetEntryKind(candidate) == FileSystemEntryKind.Symlink)
            {
                // Running as root, a job could replace even entries in "/", so follow nothing.
                if (resolved != "/" || _runningAsRoot)
                    throw new IOException($"refusing to follow {candidate}: only symlinks directly under / are followed above a directory being deleted, and none when running as root");
                candidate = _fileSystem.GetCanonicalPath(candidate);
            }
            resolved = candidate;
        }
        return resolved == "/" ? "/" + name : resolved + "/" + name;
    }

    /// <returns>The mounts still present at or beneath <paramref name="root"/>, or null when the
    /// mount table can't be read.</returns>
    private HashSet<string>? UnmountBeneathCanonical(string root)
    {
        var mounts = ReadMountsBeneath(root);
        if (mounts is null || mounts.Count == 0)
            return mounts;

        foreach (var mount in mounts
                     .OrderByDescending(m => m.Count(c => c == '/'))
                     .ThenByDescending(m => m.Length)
                     .ThenBy(m => m, StringComparer.Ordinal))
        {
            if (_recentUnmountFailures.TryGetValue(mount, out var failedAt)
                && DateTime.UtcNow - failedAt < UnmountRetryDelay)
            {
                _logger.LogWarning("Not retrying unmount of {MountPoint} under {Root}: it failed at {FailedAt:O}", mount, root, failedAt);
                continue;
            }

            if (_fileSystem.TryUnmount(mount, _unmountTimeout, out var error))
            {
                _recentUnmountFailures.TryRemove(mount, out _);
                _logger.LogInformation("Unmounted {MountPoint} so {Root} can be cleaned up", mount, root);
            }
            else
            {
                _recentUnmountFailures[mount] = DateTime.UtcNow;
                _logger.LogWarning("Could not unmount {MountPoint} under {Root}: {Error}", mount, root, error);
            }
        }

        // If the table can't be re-read, assume nothing was unmounted.
        var remaining = ReadMountsBeneath(root) ?? mounts;
        foreach (var mount in remaining.OrderBy(m => m, StringComparer.Ordinal))
        {
            _logger.LogError(
                "Refusing to delete through mount point {MountPoint}: it is still mounted beneath {Root} after an unmount attempt. Left it and its contents in place; unmount it manually",
                mount, root);
        }

        return remaining;
    }

    private HashSet<string>? ReadMountsBeneath(string root)
    {
        IReadOnlyList<string> table;
        try
        {
            table = _fileSystem.GetMountPoints();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read the mount table while cleaning up {Root}", root);
            return null;
        }

        var beneath = new HashSet<string>(_pathComparer);
        foreach (var mountPoint in table)
        {
            if (string.IsNullOrEmpty(mountPoint))
                continue;

            if (IsAtOrBeneath(mountPoint, root))
                beneath.Add(mountPoint.Length > 1 ? mountPoint.TrimEnd('/') : mountPoint);

            if (mountPoint.StartsWith(MacDataVolumePrefix, StringComparison.Ordinal))
            {
                var unprefixed = "/" + mountPoint[MacDataVolumePrefix.Length..];
                if (IsAtOrBeneath(unprefixed, root))
                    beneath.Add(unprefixed.TrimEnd('/'));
            }
        }
        return beneath;
    }

    private bool IsAtOrBeneath(string candidate, string root)
    {
        var trimmed = candidate.Length > 1 ? candidate.TrimEnd('/') : candidate;
        return string.Equals(trimmed, root, _pathComparison)
            || trimmed.StartsWith(root + "/", _pathComparison);
    }

    private static string? GetParent(string path)
    {
        var index = path.LastIndexOf('/');
        return index switch
        {
            < 0 => null,
            0 => path.Length > 1 ? "/" : null,
            _ => path[..index]
        };
    }

    private static bool TryRun(Action action, string path, MountSafeDeleteResult result)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            result.AddFailure(path, ex.Message);
            return false;
        }
    }

    private void LogFailures(MountSafeDeleteResult result)
    {
        if (result.Failures.Count == 0)
            return;

        _logger.LogWarning(
            "{Count} entries under {Path} could not be deleted; first: {First}",
            result.Failures.Count, result.Path, result.Failures[0]);
    }
}

public sealed class MountSafeDeleteResult
{
    public MountSafeDeleteResult(string path)
    {
        Path = path;
    }

    public string Path { get; }

    /// <summary>True when the directory no longer exists.</summary>
    public bool Completed { get; internal set; }

    /// <summary>Mount points at or beneath the directory that could not be unmounted.</summary>
    public List<string> RemainingMounts { get; } = [];

    /// <summary>Directories on another device that the mount table did not list.</summary>
    public List<string> SkippedForeignDevices { get; } = [];

    public List<string> Failures { get; } = [];

    internal void AddFailure(string path, string message) => Failures.Add($"{path}: {message}");

    public string Describe()
    {
        var parts = new List<string>();
        if (RemainingMounts.Count > 0)
            parts.Add($"still mounted: {string.Join(", ", RemainingMounts)}");
        if (SkippedForeignDevices.Count > 0)
            parts.Add($"other device: {string.Join(", ", SkippedForeignDevices)}");
        if (Failures.Count > 0)
            parts.Add($"{Failures.Count} delete failure(s), first: {Failures[0]}");
        return parts.Count == 0 ? "incomplete" : string.Join("; ", parts);
    }
}
