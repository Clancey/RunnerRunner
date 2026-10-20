using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RunnerRunner.Agent.Services;

/// <summary>
/// Real filesystem for <see cref="MountSafeDirectoryDeleter"/>. On macOS the mount table comes
/// from getfsstat(MNT_NOWAIT) so listing it never blocks on an unresponsive network share; on
/// Linux it comes from /proc/self/mountinfo.
/// </summary>
public sealed class SystemMountSafeFileSystem : IMountSafeFileSystem
{
    public static SystemMountSafeFileSystem Instance { get; } = new();

    private SystemMountSafeFileSystem()
    {
    }

    public bool SupportsMountDetection => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    public string GetCanonicalPath(string path)
    {
        if (!SupportsMountDetection)
            return Path.GetFullPath(path);

        var resolved = Native.realpath(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero)
            throw new IOException($"realpath failed for '{path}' (errno {Marshal.GetLastPInvokeError()}).");

        try
        {
            return Marshal.PtrToStringUTF8(resolved)
                ?? throw new IOException($"realpath returned an empty path for '{path}'.");
        }
        finally
        {
            Native.free(resolved);
        }
    }

    public IReadOnlyList<string> GetMountPoints()
    {
        if (OperatingSystem.IsMacOS())
            return GetMacMountPoints();
        if (OperatingSystem.IsLinux())
            return ParseLinuxMountInfo(File.ReadAllLines("/proc/self/mountinfo"));
        throw new PlatformNotSupportedException("Mount table lookup is only implemented for macOS and Linux.");
    }

    public bool TryUnmount(string mountPoint, TimeSpan timeout, out string? error)
    {
        var psi = new ProcessStartInfo(FindUmount())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add(mountPoint);

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                error = "umount could not be started";
                return false;
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(TimeSpan.FromSeconds(5));
                }
                catch { /* may be stuck in the kernel */ }
                error = $"umount timed out after {timeout.TotalSeconds:0}s";
                return false;
            }

            process.WaitForExit();
            var output = (stderr.Result + stdout.Result).Trim();
            error = process.ExitCode == 0 ? null : $"umount exited {process.ExitCode}: {output}";
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public FileSystemEntryKind GetEntryKind(string path)
    {
        try
        {
            // readlink only — never stats the target.
            if (new FileInfo(path).LinkTarget is not null)
                return FileSystemEntryKind.Symlink;
        }
        catch (FileNotFoundException)
        {
            return FileSystemEntryKind.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return FileSystemEntryKind.Missing;
        }

        if (Directory.Exists(path))
            return FileSystemEntryKind.Directory;
        if (File.Exists(path))
            return FileSystemEntryKind.File;
        return FileSystemEntryKind.Missing;
    }

    public void DeleteFile(string path) => File.Delete(path);

    public IMountSafeDirectoryHandle OpenDirectory(string path)
    {
        if (!SupportsMountDetection)
            throw new PlatformNotSupportedException("Descriptor-relative deletion is only implemented for macOS and Linux.");

        var fd = Native.open(path, Native.DirectoryOpenFlags);
        if (fd < 0)
            throw Native.Error("open", path);
        return UnixDirectoryHandle.Create(fd, path);
    }

    internal static long? ParseLinuxFdInfoMountId(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (line.StartsWith("mnt_id:", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan(7).Trim(), out var mountId))
                return mountId;
        }
        return null;
    }

    internal static IReadOnlyList<string> ParseLinuxMountInfo(IEnumerable<string> lines)
    {
        var result = new List<string>();
        foreach (var line in lines)
        {
            // "<id> <parent> <major:minor> <root> <mount point> <options> ..."
            var fields = line.Split(' ');
            if (fields.Length > 4)
                result.Add(UnescapeMountInfoField(fields[4]));
        }
        return result;
    }

    internal static string UnescapeMountInfoField(string field)
    {
        if (!field.Contains('\\'))
            return field;

        // The kernel octal-escapes only space, tab, newline and backslash, all ASCII.
        var builder = new StringBuilder(field.Length);
        for (var i = 0; i < field.Length; i++)
        {
            if (field[i] == '\\' && i + 3 < field.Length
                && IsOctal(field[i + 1]) && IsOctal(field[i + 2]) && IsOctal(field[i + 3]))
            {
                builder.Append((char)(((field[i + 1] - '0') << 6) | ((field[i + 2] - '0') << 3) | (field[i + 3] - '0')));
                i += 3;
                continue;
            }

            builder.Append(field[i]);
        }
        return builder.ToString();

        static bool IsOctal(char c) => c is >= '0' and <= '7';
    }

    private static IReadOnlyList<string> GetMacMountPoints()
    {
        // struct statfs (64-bit inode layout, which getfsstat64 always uses): 2168 bytes with
        // f_mntonname[MAXPATHLEN] at offset 88.
        const int StatfsSize = 2168;
        const int MountOnNameOffset = 88;
        const int MaxPathLength = 1024;
        const int MNT_NOWAIT = 2;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var count = Native.getfsstat64(IntPtr.Zero, 0, MNT_NOWAIT);
            if (count < 0)
                throw new IOException($"getfsstat failed (errno {Marshal.GetLastPInvokeError()}).");

            var capacity = count + 16;
            var buffer = Marshal.AllocHGlobal(capacity * StatfsSize);
            try
            {
                var filled = Native.getfsstat64(buffer, capacity * StatfsSize, MNT_NOWAIT);
                if (filled < 0)
                    throw new IOException($"getfsstat failed (errno {Marshal.GetLastPInvokeError()}).");
                if (filled >= capacity)
                    continue; // table grew between calls; the result may be truncated

                var result = new List<string>(filled);
                var name = new byte[MaxPathLength];
                for (var i = 0; i < filled; i++)
                {
                    Marshal.Copy(buffer + i * StatfsSize + MountOnNameOffset, name, 0, MaxPathLength);
                    var length = Array.IndexOf(name, (byte)0);
                    result.Add(Encoding.UTF8.GetString(name, 0, length < 0 ? MaxPathLength : length));
                }
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        throw new IOException("getfsstat kept returning a full buffer; the mount table is changing too fast to read.");
    }

    private static string FindUmount()
    {
        foreach (var candidate in new[] { "/sbin/umount", "/usr/sbin/umount", "/bin/umount", "/usr/bin/umount" })
        {
            if (File.Exists(candidate))
                return candidate;
        }
        return "umount";
    }

    private sealed class UnixDirectoryHandle : IMountSafeDirectoryHandle
    {
        private int _fd;

        private UnixDirectoryHandle(int fd, string path, ulong deviceId, long? mountId)
        {
            _fd = fd;
            Path = path;
            DeviceId = deviceId;
            MountId = mountId;
        }

        public string Path { get; }
        public ulong DeviceId { get; }
        public long? MountId { get; }

        /// <summary>Takes ownership of <paramref name="fd"/>.</summary>
        public static UnixDirectoryHandle Create(int fd, string path)
        {
            try
            {
                var deviceId = Native.GetDeviceId(fd, path);
                long? mountId = null;
                if (OperatingSystem.IsLinux())
                {
                    // A same-device bind mount is only distinguishable by mount id, so without it
                    // (kernels before 3.15, /proc unavailable) the walk can't be made safe: fail closed.
                    mountId = ParseLinuxFdInfoMountId(File.ReadLines($"/proc/self/fdinfo/{fd}"))
                        ?? throw new IOException($"Could not determine the mount id of '{path}' from /proc/self/fdinfo; refusing to walk it.");
                }
                return new UnixDirectoryHandle(fd, path, deviceId, mountId);
            }
            catch
            {
                Native.close(fd);
                throw;
            }
        }

        public IReadOnlyList<string> ReadEntryNames()
        {
            // A fresh open file description of the same directory, so the listing always starts
            // at the beginning and never shares a read offset with this handle.
            var listFd = Native.openat(Descriptor, ".", Native.DirectoryOpenFlags);
            if (listFd < 0)
                throw Native.Error("openat", Path);

            var stream = Native.FdOpenDir(listFd);
            if (stream == IntPtr.Zero)
            {
                var error = Native.Error("fdopendir", Path);
                Native.close(listFd);
                throw error;
            }

            try
            {
                var names = new List<string>();
                while (true)
                {
                    var entry = Native.ReadDir(stream, out var errno);
                    if (entry == IntPtr.Zero)
                    {
                        if (errno != 0)
                            throw Native.Error("readdir", Path, errno);
                        return names;
                    }

                    var name = Native.GetEntryName(entry);
                    if (name is not ("." or ".."))
                        names.Add(name);
                }
            }
            finally
            {
                Native.closedir(stream);
            }
        }

        public IMountSafeDirectoryHandle? OpenChildDirectory(string name)
        {
            var childPath = ChildPath(name);
            var fd = Native.openat(Descriptor, name, Native.DirectoryOpenFlags);
            if (fd < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (Native.IsNotADirectory(errno))
                    return null;
                throw Native.Error("openat", childPath, errno);
            }
            return Create(fd, childPath);
        }

        public void DeleteChild(string name) => Unlink(name, 0);

        public void DeleteChildDirectory(string name) => Unlink(name, Native.AtRemoveDir);

        public void Dispose()
        {
            var fd = Interlocked.Exchange(ref _fd, -1);
            if (fd >= 0)
                Native.close(fd);
        }

        private int Descriptor => _fd >= 0 ? _fd : throw new ObjectDisposedException(Path);

        private void Unlink(string name, int flags)
        {
            var childPath = ChildPath(name);
            if (Native.unlinkat(Descriptor, name, flags) == 0)
                return;

            var errno = Marshal.GetLastPInvokeError();
            if (errno != Native.ENOENT)
                throw Native.Error("unlinkat", childPath, errno);
        }

        private string ChildPath(string name)
        {
            if (name.Length == 0 || name is "." or ".." || name.Contains('/'))
                throw new ArgumentException($"'{name}' is not a directory entry name.", nameof(name));
            return Path == "/" ? "/" + name : Path + "/" + name;
        }
    }

    private static class Native
    {
        public const int ENOENT = 2;
        private const int ENOTDIR = 20;
        private const int EMLINK = 31;
        private static readonly int ELOOP = OperatingSystem.IsMacOS() ? 62 : 40;

        public static readonly int AtRemoveDir = OperatingSystem.IsMacOS() ? 0x0080 : 0x0200;

        // O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC
        public static readonly int DirectoryOpenFlags = OperatingSystem.IsMacOS()
            ? 0x100000 | 0x0100 | 0x1000000
            : RuntimeInformation.ProcessArchitecture is Architecture.Arm or Architecture.Arm64 or Architecture.Ppc64le
                ? 0x4000 | 0x8000 | 0x80000
                : 0x10000 | 0x20000 | 0x80000;

        // Intel macOS keeps the 32-bit-inode dirent behind the unsuffixed symbols.
        private static readonly bool UseInode64Suffix =
            OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

        /// <summary>openat with O_NOFOLLOW fails this way when the entry is a symlink or not a directory.</summary>
        public static bool IsNotADirectory(int errno) => errno == ENOENT || errno == ENOTDIR || errno == ELOOP || errno == EMLINK;

        public static IOException Error(string operation, string path) => Error(operation, path, Marshal.GetLastPInvokeError());

        public static IOException Error(string operation, string path, int errno)
            => new($"{operation} failed for '{path}': {Marshal.GetPInvokeErrorMessage(errno)} (errno {errno})");

        [DllImport("libc", SetLastError = true)]
        public static extern IntPtr realpath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr resolved);

        [DllImport("libc")]
        public static extern void free(IntPtr ptr);

        [DllImport("libc", SetLastError = true)]
        public static extern int getfsstat64(IntPtr buf, int bufsize, int flags);

        // Variadic in C, but the mode argument is only read with O_CREAT.
        [DllImport("libc", SetLastError = true)]
        public static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", SetLastError = true)]
        public static extern int openat(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", SetLastError = true)]
        public static extern int unlinkat(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", SetLastError = true)]
        public static extern int close(int fd);

        [DllImport("libc", SetLastError = true)]
        public static extern int closedir(IntPtr dir);

        [DllImport("libc", SetLastError = true, EntryPoint = "fdopendir")]
        private static extern IntPtr fdopendir(int fd);

        [DllImport("libc", SetLastError = true, EntryPoint = "fdopendir$INODE64")]
        private static extern IntPtr fdopendir_inode64(int fd);

        [DllImport("libc", SetLastError = true, EntryPoint = "readdir")]
        private static extern IntPtr readdir(IntPtr dir);

        [DllImport("libc", SetLastError = true, EntryPoint = "readdir$INODE64")]
        private static extern IntPtr readdir_inode64(IntPtr dir);

        [DllImport("libc", SetLastError = true, EntryPoint = "readdir64")]
        private static extern IntPtr readdir64(IntPtr dir);

        [DllImport("libc", SetLastError = true, EntryPoint = "fstat")]
        private static extern int fstat(int fd, byte[] buf);

        // glibc < 2.33 exports only the versioned wrapper.
        [DllImport("libc", SetLastError = true, EntryPoint = "__fxstat")]
        private static extern int __fxstat(int version, int fd, byte[] buf);

        private static bool _useFxstat;
        private static bool _useReaddir;

        public static IntPtr FdOpenDir(int fd) => UseInode64Suffix ? fdopendir_inode64(fd) : fdopendir(fd);

        /// <summary>Returns the next dirent, or zero at the end (errno 0) or on error.</summary>
        public static IntPtr ReadDir(IntPtr dir, out int errno)
        {
            IntPtr entry;
            if (OperatingSystem.IsMacOS())
            {
                entry = UseInode64Suffix ? readdir_inode64(dir) : readdir(dir);
            }
            else if (!_useReaddir)
            {
                try
                {
                    entry = readdir64(dir);
                }
                catch (EntryPointNotFoundException) when (IntPtr.Size == 8)
                {
                    // 64-bit libcs without the LFS alias use the same layout for readdir.
                    _useReaddir = true;
                    entry = readdir(dir);
                }
            }
            else
            {
                entry = readdir(dir);
            }

            errno = entry == IntPtr.Zero ? Marshal.GetLastPInvokeError() : 0;
            return entry;
        }

        public static string GetEntryName(IntPtr entry)
        {
            if (OperatingSystem.IsMacOS())
            {
                // struct dirent (64-bit inode): u64 d_ino, u64 d_seekoff, u16 d_reclen,
                // u16 d_namlen, u8 d_type, char d_name[1024].
                var length = (ushort)Marshal.ReadInt16(entry, 18);
                return Marshal.PtrToStringUTF8(entry + 21, length);
            }

            // struct dirent64: u64 d_ino, i64 d_off, u16 d_reclen, u8 d_type, char d_name[256].
            return Marshal.PtrToStringUTF8(entry + 19) ?? string.Empty;
        }

        public static ulong GetDeviceId(int fd, string path)
        {
            // st_dev is the first field of struct stat on every supported ABI: a 32-bit dev_t on
            // macOS and a 64-bit dev_t on Linux. The buffer is larger than any struct stat.
            var buffer = new byte[512];
            if (Fstat(fd, buffer) != 0)
                throw Error("fstat", path);

            return OperatingSystem.IsMacOS()
                ? BitConverter.ToUInt32(buffer, 0)
                : BitConverter.ToUInt64(buffer, 0);
        }

        private static int Fstat(int fd, byte[] buffer)
        {
            if (!_useFxstat)
            {
                try
                {
                    return fstat(fd, buffer);
                }
                catch (EntryPointNotFoundException) when (OperatingSystem.IsLinux())
                {
                    _useFxstat = true;
                }
            }

            // _STAT_VER: 1 on x86_64, 0 on aarch64 and other generic-ABI architectures.
            var version = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 1 : 0;
            return __fxstat(version, fd, buffer);
        }
    }
}
