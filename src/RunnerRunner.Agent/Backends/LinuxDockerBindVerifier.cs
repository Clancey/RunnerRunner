using System.Net.Sockets;
using System.Runtime.InteropServices;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Agent.Backends;

internal static class LinuxDockerBindVerifier
{
    internal static void Verify(Uri endpoint, IReadOnlyList<string> sources)
    {
        if (!OperatingSystem.IsLinux() || File.Exists("/.dockerenv")
            || string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)
            || endpoint.Scheme != "unix")
            throw new InvalidOperationException("Verified Docker binds require a native Linux broker and a local Unix Docker endpoint.");

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Connect(new UnixDomainSocketEndPoint(endpoint.LocalPath));
        var length = (uint)Marshal.SizeOf<PeerCredentials>();
        if (getsockopt(socket.SafeHandle.DangerousGetHandle().ToInt32(), 1, 17, out var peer, ref length) != 0
            || length != Marshal.SizeOf<PeerCredentials>() || peer.Pid <= 0 || peer.Uid != 0)
            throw new InvalidOperationException("Cannot authenticate the local Docker daemon peer.");

        var daemon = $"/proc/{peer.Pid}";
        if (File.ReadAllText($"{daemon}/comm").Trim() != "dockerd")
            throw new InvalidOperationException("Verified Docker binds require a direct connection to dockerd.");
        ValidateNamespace(
            new FileInfo("/proc/self/ns/mnt").LinkTarget, new FileInfo($"{daemon}/ns/mnt").LinkTarget,
            new DirectoryInfo("/proc/self/root").LinkTarget, new DirectoryInfo($"{daemon}/root").LinkTarget);

        var mounts = File.ReadAllText($"{daemon}/mountinfo");
        ValidateSources(sources, mounts);
        if (File.ReadAllText($"{daemon}/mountinfo") != mounts)
            throw new InvalidOperationException("Docker mount topology changed during source verification.");
    }

    internal static void ValidateNamespace(string? workerMount, string? daemonMount, string? workerRoot, string? daemonRoot)
    {
        if (string.IsNullOrEmpty(workerMount) || !workerMount.StartsWith("mnt:[", StringComparison.Ordinal)
            || workerMount != daemonMount || workerRoot != "/" || daemonRoot != "/")
            throw new InvalidOperationException("Broker and Docker daemon must share the host root and mount namespace.");
    }

    internal static void ValidateSources(IReadOnlyList<string> sources, string mountInfo)
    {
        var mounts = ParseMounts(mountInfo);
        foreach (var source in sources)
        {
            if (!DockerBindMount.IsCanonicalPath(source))
                throw new InvalidOperationException("Docker bind source is not a canonical Linux path.");
            var current = "";
            foreach (var segment in source.Split('/').Skip(1))
            {
                current += "/" + segment;
                var directory = new DirectoryInfo(current);
                directory.Refresh();
                if (directory.LinkTarget != null || !directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException($"Docker bind source ancestry is missing, not a directory, or a symbolic link: {current}");
            }
            var containing = mounts.Where(mount => mount.Path == "/" || source == mount.Path
                    || source.StartsWith(mount.Path + "/", StringComparison.Ordinal))
                .ToList();
            var longest = containing.Max(mount => mount.Path.Length);
            if (containing.Any(mount => mount.Path.Length == longest && mount.Root != "/"))
                throw new InvalidOperationException($"Docker bind source '{source}' is beneath an unsupported subdirectory mount or subvolume root.");
            if (mounts.Any(mount => mount.Path.StartsWith(source + "/", StringComparison.Ordinal)))
                throw new InvalidOperationException($"Docker bind source '{source}' has nested mount topology.");
        }
    }

    internal static List<(string Path, string Root)> ParseMounts(string mountInfo)
    {
        var mounts = new List<(string Path, string Root)>();
        foreach (var line in mountInfo.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(' ');
            var separator = Array.IndexOf(fields, "-");
            if (separator < 6 || fields.Length < separator + 4 || !int.TryParse(fields[0], out _)
                || !int.TryParse(fields[1], out _) || !fields[2].Contains(':')
                || !fields[3].StartsWith('/') || !fields[4].StartsWith('/'))
                throw new InvalidOperationException("Cannot parse daemon mount topology.");
            mounts.Add((Unescape(fields[4]), Unescape(fields[3])));
        }
        if (!mounts.Any(mount => mount.Path == "/"))
            throw new InvalidOperationException("Daemon mount topology has no host root.");
        return mounts;
    }

    private static string Unescape(string path) =>
        path.Replace("\\040", " ").Replace("\\011", "\t").Replace("\\012", "\n").Replace("\\134", "\\");

    [StructLayout(LayoutKind.Sequential)]
    private struct PeerCredentials
    {
        public int Pid;
        public uint Uid;
        public uint Gid;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int getsockopt(int socket, int level, int option, out PeerCredentials value, ref uint length);
}
