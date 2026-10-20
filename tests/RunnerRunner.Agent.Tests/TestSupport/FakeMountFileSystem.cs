using RunnerRunner.Agent.Services;

namespace RunnerRunner.Agent.Tests.TestSupport;

/// <summary>
/// In-memory filesystem with devices, mount ids and a mount table, recording every operation so
/// tests can assert what the deleter touched. Mounts behave like the kernel's: a mount point's
/// directory carries the mounted device, unmounting hides the mounted contents again, a mount
/// with another mount beneath it is busy, and a mount point can't be rmdir'd while mounted.
/// </summary>
internal sealed class FakeMountFileSystem : IMountSafeFileSystem
{
    private const ulong RootDevice = 1;
    private const long RootMountId = 1;

    private sealed class Node(FileSystemEntryKind kind, ulong device, long mountId)
    {
        public FileSystemEntryKind Kind { get; } = kind;
        public ulong Device { get; } = device;
        public long MountId { get; } = mountId;
    }

    private sealed class Mount
    {
        public required string Path { get; init; }
        public required string ReportedPath { get; init; }
        public required bool CanUnmount { get; init; }
        public required Node Covered { get; init; }
    }

    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal) { ["/"] = new(FileSystemEntryKind.Directory, RootDevice, RootMountId) };
    private readonly List<Mount> _mounts = [];
    private readonly Dictionary<string, string> _canonical = new(StringComparer.Ordinal);
    private long _nextMountId = 100;

    public bool SupportsMountDetection { get; set; } = true;
    public bool MountTableUnavailable { get; set; }

    /// <summary>Whether handles report a mount id (Linux) or not (macOS).</summary>
    public bool ExposesMountIds { get; set; } = true;

    /// <summary>Runs just before a child directory is opened, to simulate concurrent changes.</summary>
    public Action<string>? BeforeOpenChild { get; set; }

    public List<string> UnmountCalls { get; } = [];
    public List<string> Enumerated { get; } = [];
    public List<string> Inspected { get; } = [];
    public List<string> Deleted { get; } = [];
    public int OpenHandles { get; private set; }

    /// <summary>Detached contents of unmounted filesystems, which must never have been deleted.</summary>
    public HashSet<string> UnmountedContents { get; } = new(StringComparer.Ordinal);

    public FakeMountFileSystem Directory(string path, ulong? device = null, long? mountId = null)
    {
        Add(path, FileSystemEntryKind.Directory, device, mountId);
        return this;
    }

    public FakeMountFileSystem File(string path, ulong? device = null)
    {
        Add(path, FileSystemEntryKind.File, device, null);
        return this;
    }

    public FakeMountFileSystem Symlink(string path)
    {
        Add(path, FileSystemEntryKind.Symlink, null, null);
        return this;
    }

    public FakeMountFileSystem Alias(string path, string canonicalPath)
    {
        _canonical[path] = canonicalPath;
        return this;
    }

    /// <summary>Mounts a new filesystem at <paramref name="path"/>; add its contents afterwards.</summary>
    public FakeMountFileSystem MountAt(string path, ulong device, bool canUnmount, string? reportedAs = null)
    {
        Add(path, FileSystemEntryKind.Directory, null, null);
        _mounts.Add(new Mount
        {
            Path = path,
            ReportedPath = reportedAs ?? path,
            CanUnmount = canUnmount,
            Covered = _nodes[path]
        });
        _nodes[path] = new Node(FileSystemEntryKind.Directory, device, _nextMountId++);
        return this;
    }

    /// <summary>Replaces whatever is at <paramref name="path"/> (and beneath it) with a symlink.</summary>
    public void ReplaceWithSymlink(string path)
    {
        foreach (var existing in PathsAtOrBeneath(path).ToList())
            _nodes.Remove(existing);
        Add(path, FileSystemEntryKind.Symlink, null, null);
    }

    public bool Exists(string path) => _nodes.ContainsKey(path);

    public bool IsMounted(string path) => _mounts.Any(m => m.Path == path);

    public IEnumerable<string> PathsAtOrBeneath(string path)
        => _nodes.Keys.Where(p => p == path || p.StartsWith(path + "/", StringComparison.Ordinal));

    public string GetCanonicalPath(string path)
    {
        Inspected.Add(path);
        return Resolve(path);
    }

    /// <summary>Like realpath: replaces an aliased (symlinked) prefix with its target.</summary>
    private string Resolve(string path)
    {
        foreach (var (alias, target) in _canonical)
        {
            if (path == alias)
                return target;
            if (path.StartsWith(alias + "/", StringComparison.Ordinal))
                return target + path[alias.Length..];
        }
        return path;
    }

    public IReadOnlyList<string> GetMountPoints()
    {
        if (MountTableUnavailable)
            throw new IOException("mount table unavailable");
        return ["/", "/Volumes/Unrelated", .. _mounts.Select(m => m.ReportedPath)];
    }

    public bool TryUnmount(string mountPoint, TimeSpan timeout, out string? error)
    {
        UnmountCalls.Add(mountPoint);
        var mount = _mounts.SingleOrDefault(m => m.ReportedPath == mountPoint || m.Path == mountPoint);
        if (mount is null)
        {
            error = "not currently mounted";
            return false;
        }

        if (!mount.CanUnmount || _mounts.Any(m => m != mount && m.Path.StartsWith(mount.Path + "/", StringComparison.Ordinal)))
        {
            error = "Resource busy";
            return false;
        }

        foreach (var path in PathsAtOrBeneath(mount.Path).Where(p => p != mount.Path).ToList())
        {
            _nodes.Remove(path);
            UnmountedContents.Add(path);
        }
        _nodes[mount.Path] = mount.Covered;
        _mounts.Remove(mount);
        error = null;
        return true;
    }

    public FileSystemEntryKind GetEntryKind(string path)
    {
        Inspected.Add(path);
        if (_canonical.ContainsKey(path))
            return FileSystemEntryKind.Symlink;
        // Like lstat: intermediate symlinks (here, aliases) are followed.
        path = Resolve(path);
        return _nodes.TryGetValue(path, out var node) ? node.Kind : FileSystemEntryKind.Missing;
    }

    public void DeleteFile(string path)
    {
        if (!_nodes.TryGetValue(path, out var node) || node.Kind is not (FileSystemEntryKind.File or FileSystemEntryKind.Symlink))
            throw new IOException($"{path} is not a file or symlink");
        _nodes.Remove(path);
        Deleted.Add(path);
    }

    public IMountSafeDirectoryHandle OpenDirectory(string path)
    {
        Inspected.Add(path);
        if (!_nodes.TryGetValue(path, out var node))
            throw new DirectoryNotFoundException(path);
        if (node.Kind != FileSystemEntryKind.Directory)
            throw new IOException($"{path}: Not a directory");
        return new Handle(this, path, node);
    }

    private sealed class Handle : IMountSafeDirectoryHandle
    {
        private readonly FakeMountFileSystem _fs;
        private readonly Node _node;
        private bool _disposed;

        public Handle(FakeMountFileSystem fs, string path, Node node)
        {
            _fs = fs;
            _node = node;
            Path = path;
            fs.OpenHandles++;
        }

        public string Path { get; }
        public ulong DeviceId => _node.Device;
        public long? MountId => _fs.ExposesMountIds ? _node.MountId : null;

        public IReadOnlyList<string> ReadEntryNames()
        {
            EnsureCurrent();
            _fs.Enumerated.Add(Path);
            return _fs._nodes.Keys
                .Where(p => ParentOf(p) == Path)
                .Select(p => p[(Path.Length == 1 ? 1 : Path.Length + 1)..])
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
        }

        public IMountSafeDirectoryHandle? OpenChildDirectory(string name)
        {
            EnsureCurrent();
            var childPath = Child(name);
            _fs.BeforeOpenChild?.Invoke(childPath);
            _fs.Inspected.Add(childPath);
            // Like openat(O_DIRECTORY | O_NOFOLLOW): a symlink is not a directory.
            return _fs._nodes.TryGetValue(childPath, out var node) && node.Kind == FileSystemEntryKind.Directory
                ? new Handle(_fs, childPath, node)
                : null;
        }

        public void DeleteChild(string name)
        {
            EnsureCurrent();
            var childPath = Child(name);
            if (!_fs._nodes.TryGetValue(childPath, out var node))
                return;
            if (node.Kind == FileSystemEntryKind.Directory)
                throw new IOException($"{childPath}: Is a directory");
            _fs._nodes.Remove(childPath);
            _fs.Deleted.Add(childPath);
        }

        public void DeleteChildDirectory(string name)
        {
            EnsureCurrent();
            var childPath = Child(name);
            if (!_fs._nodes.TryGetValue(childPath, out var node))
                return;
            if (node.Kind != FileSystemEntryKind.Directory)
                throw new IOException($"{childPath}: Not a directory");
            if (_fs.IsMounted(childPath))
                throw new IOException($"{childPath}: Resource busy");
            if (_fs._nodes.Keys.Any(p => ParentOf(p) == childPath))
                throw new IOException($"{childPath}: Directory not empty");
            _fs._nodes.Remove(childPath);
            _fs.Deleted.Add(childPath);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _fs.OpenHandles--;
        }

        private string Child(string name) => Path == "/" ? "/" + name : Path + "/" + name;

        // The fake keys contents by path, so it can only model a handle whose path still leads to
        // the directory it opened. Tests that replace an open directory would need a real inode model.
        private void EnsureCurrent()
        {
            if (_disposed)
                throw new ObjectDisposedException(Path);
            if (!_fs._nodes.TryGetValue(Path, out var current) || !ReferenceEquals(current, _node))
                throw new InvalidOperationException($"The fake can't model {Path} being replaced while it is open.");
        }
    }

    private void Add(string path, FileSystemEntryKind kind, ulong? device, long? mountId)
    {
        var parent = ParentOf(path);
        if (parent is not null && !_nodes.ContainsKey(parent))
            Add(parent, FileSystemEntryKind.Directory, null, null);

        var inherited = parent is not null ? _nodes[parent] : null;
        _nodes[path] = new Node(kind, device ?? inherited?.Device ?? RootDevice, mountId ?? inherited?.MountId ?? RootMountId);
    }

    private static string? ParentOf(string path)
    {
        if (path == "/")
            return null;
        var index = path.LastIndexOf('/');
        return index == 0 ? "/" : path[..index];
    }
}
