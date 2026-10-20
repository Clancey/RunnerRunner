namespace RunnerRunner.Agent.Services;

public enum FileSystemEntryKind
{
    Missing,
    File,
    Directory,
    Symlink
}

/// <summary>
/// The filesystem primitives <see cref="MountSafeDirectoryDeleter"/> needs. Abstracted so the
/// mount and device checks can be exercised without real mounts (which usually need root).
/// </summary>
/// <remarks>
/// The tree walk goes through open directory handles rather than path strings, so a directory
/// that is swapped for a symlink or has something mounted over it while the walk is running can't
/// redirect the deletion somewhere else.
/// </remarks>
public interface IMountSafeFileSystem
{
    /// <summary>
    /// False where mount-aware deletion isn't implemented (Windows); the deleter then falls back
    /// to <see cref="Directory.Delete(string, bool)"/>, which does not traverse reparse points.
    /// </summary>
    bool SupportsMountDetection { get; }

    /// <summary>Resolves symlinks in every component of an existing path (realpath(3)).</summary>
    string GetCanonicalPath(string path);

    /// <summary>All current mount points, as recorded by the kernel mount table.</summary>
    IReadOnlyList<string> GetMountPoints();

    /// <summary>Normal (non-forced) unmount. Returns false if the mount is still present.</summary>
    bool TryUnmount(string mountPoint, TimeSpan timeout, out string? error);

    /// <summary>Classifies a path without following a final symlink.</summary>
    FileSystemEntryKind GetEntryKind(string path);

    /// <summary>Removes a file or a symlink (never its target).</summary>
    void DeleteFile(string path);

    /// <summary>
    /// Opens an existing directory without following a final symlink. Throws if
    /// <paramref name="path"/> is not a directory.
    /// </summary>
    IMountSafeDirectoryHandle OpenDirectory(string path);
}

/// <summary>An open directory; every operation is relative to it (openat/unlinkat).</summary>
public interface IMountSafeDirectoryHandle : IDisposable
{
    /// <summary>The path the directory was opened through, for matching mounts and logging.</summary>
    string Path { get; }

    /// <summary>st_dev of the open directory (fstat).</summary>
    ulong DeviceId { get; }

    /// <summary>
    /// The kernel's id for the mount the open directory is on, where the platform exposes it
    /// (Linux mnt_id). A bind mount keeps its source's st_dev but gets its own mount id.
    /// </summary>
    long? MountId { get; }

    /// <summary>Names of the directory's entries, excluding "." and "..".</summary>
    IReadOnlyList<string> ReadEntryNames();

    /// <summary>
    /// Opens a child directory without following a symlink. Returns null when the child no
    /// longer exists or is not a directory (a symlink to a directory is not a directory here).
    /// </summary>
    IMountSafeDirectoryHandle? OpenChildDirectory(string name);

    /// <summary>Removes a child file or symlink (never its target). A missing child is ignored.</summary>
    void DeleteChild(string name);

    /// <summary>Removes an empty child directory. A missing child is ignored.</summary>
    void DeleteChildDirectory(string name);
}
