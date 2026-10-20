using Microsoft.Extensions.Logging;
using RunnerRunner.Agent.Services;
using RunnerRunner.Agent.Tests.TestSupport;

namespace RunnerRunner.Agent.Tests.Services;

public sealed class MountSafeDirectoryDeleterTests : IDisposable
{
    private const string Instance = "/home/rr/.runnerrunner/instances/rr-53d8adc8282263fc";
    private const string Tower = Instance + "/_work/_temp/visionos.zGvzSG/tower.9tkeMx";
    private const string Sentinel = Tower + "/appdata/sentinel.txt";

    private readonly ListLogger<MountSafeDirectoryDeleterTests> _logger = new();
    private readonly List<FakeMountFileSystem> _fakes = [];

    public void Dispose()
    {
        foreach (var fs in _fakes)
            Assert.Equal(0, fs.OpenHandles);
    }

    [Fact]
    public void Delete_StuckMount_IsLeftUntouchedAndEverythingElseIsDeleted()
    {
        var fs = InstanceTree().MountAt(Tower, device: 7, canUnmount: false).File(Sentinel);

        var result = Deleter(fs).Delete(Instance);

        Assert.False(result.Completed);
        Assert.Equal([Tower], result.RemainingMounts);
        Assert.Equal([Tower], fs.UnmountCalls);
        AssertUntouched(fs, Tower);
        Assert.True(fs.Exists(Sentinel));
        Assert.False(fs.Exists(Instance + "/run.sh"));
        Assert.False(fs.Exists(Instance + "/_work/repo"));
        Assert.True(fs.Exists(Instance + "/_work/_temp/visionos.zGvzSG"), "ancestors of the stuck mount stay");
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains(Tower, StringComparison.Ordinal));
        Assert.Throws<IOException>(() => Deleter(fs).DeleteOrThrow(Instance));
    }

    [Fact]
    public void Delete_StuckMountOnTheSameDevice_IsSkippedBecauseTheMountTableListsIt()
    {
        // A Linux bind mount reports the same st_dev as its parent, so only the table catches it.
        var fs = InstanceTree().MountAt(Tower, device: 1, canUnmount: false).File(Sentinel);

        var result = Deleter(fs).Delete(Instance);

        Assert.False(result.Completed);
        Assert.Equal([Tower], result.RemainingMounts);
        AssertUntouched(fs, Tower);
        Assert.True(fs.Exists(Sentinel));
    }

    [Fact]
    public void Delete_UnmountsNestedMountsDeepestFirst_ThenDeletesTheWholeTree()
    {
        var outer = Instance + "/_work/_temp/outer";
        var inner = outer + "/nested/inner";
        var sibling = Instance + "/_work/_temp/zsibling";
        var fs = InstanceTree()
            .MountAt(outer, device: 2, canUnmount: true).File(outer + "/outer.txt")
            .MountAt(inner, device: 3, canUnmount: true).File(inner + "/inner.txt")
            .MountAt(sibling, device: 4, canUnmount: true).File(sibling + "/sibling.txt");

        var result = Deleter(fs).Delete(Instance);

        Assert.True(result.Completed, result.Describe());
        Assert.Equal(inner, fs.UnmountCalls[0]);
        Assert.Equal(new[] { inner, outer, sibling }.Order(StringComparer.Ordinal), fs.UnmountCalls.Order(StringComparer.Ordinal));
        Assert.Empty(fs.PathsAtOrBeneath(Instance));
        Assert.Contains(outer + "/outer.txt", fs.UnmountedContents);
        Assert.Contains(inner + "/inner.txt", fs.UnmountedContents);
        Assert.Contains(sibling + "/sibling.txt", fs.UnmountedContents);
        Assert.DoesNotContain(fs.Deleted, p => fs.UnmountedContents.Contains(p));
    }

    [Fact]
    public void Delete_NeverDescendsIntoAnotherDeviceMissingFromTheMountTable()
    {
        var foreign = Instance + "/_work/_temp/unlisted";
        var fs = InstanceTree().Directory(foreign, device: 9).File(foreign + "/data.txt");

        var result = Deleter(fs).Delete(Instance);

        Assert.False(result.Completed);
        Assert.Equal([foreign], result.SkippedForeignDevices);
        Assert.Empty(fs.UnmountCalls);
        Assert.DoesNotContain(foreign, fs.Enumerated);
        Assert.True(fs.Exists(foreign + "/data.txt"));
        Assert.False(fs.Exists(Instance + "/run.sh"));
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains(foreign, StringComparison.Ordinal));
    }

    [Fact]
    public void Delete_WithoutAMountTable_DeletesNothing()
    {
        // A same-device bind mount is invisible to st_dev, so an unreadable table must fail closed.
        var fs = InstanceTree().MountAt(Tower, device: 1, canUnmount: true).File(Sentinel);
        fs.MountTableUnavailable = true;

        var result = Deleter(fs).Delete(Instance);

        Assert.False(result.Completed);
        Assert.Empty(fs.UnmountCalls);
        Assert.Empty(fs.Deleted);
        Assert.Empty(fs.Enumerated);
        Assert.True(fs.Exists(Sentinel));
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("mount table", StringComparison.Ordinal));
    }

    [Fact]
    public void Delete_UnlistedBindMountOnTheSameDevice_IsCaughtByItsMountId()
    {
        var bind = Instance + "/_work/_temp/bind";
        var fs = InstanceTree().Directory(bind, device: 1, mountId: 50).File(bind + "/data.txt");

        var result = Deleter(fs).Delete(Instance);

        Assert.False(result.Completed);
        Assert.Equal([bind], result.SkippedForeignDevices);
        Assert.DoesNotContain(bind, fs.Enumerated);
        Assert.True(fs.Exists(bind + "/data.txt"));
        Assert.False(fs.Exists(Instance + "/run.sh"));
    }

    [Fact]
    public void Delete_ChildSwappedForASymlinkAfterListing_RemovesTheLinkWithoutFollowingIt()
    {
        var swapped = Instance + "/_work/repo";
        var fs = InstanceTree().File("/Volumes/share/data.txt");
        fs.BeforeOpenChild = path =>
        {
            if (path == swapped)
                fs.ReplaceWithSymlink(swapped);
        };

        var result = Deleter(fs).Delete(Instance);

        Assert.True(result.Completed, result.Describe());
        Assert.Contains(swapped, fs.Deleted);
        Assert.DoesNotContain(swapped, fs.Enumerated);
        Assert.True(fs.Exists("/Volumes/share/data.txt"));
    }

    [Fact]
    public void Delete_MountAppearingAfterTheMountTableWasRead_IsNotEntered()
    {
        var late = Instance + "/_work/repo";
        var fs = InstanceTree();
        fs.BeforeOpenChild = path =>
        {
            if (path == late && !fs.IsMounted(late))
                fs.MountAt(late, device: 8, canUnmount: true).File(late + "/share.txt");
        };

        var result = Deleter(fs).Delete(Instance);

        Assert.False(result.Completed);
        Assert.Empty(fs.UnmountCalls);
        Assert.Equal([late], result.SkippedForeignDevices);
        // Opening the mount root is how the walker sees the new filesystem; it must stop there.
        Assert.True(fs.IsMounted(late));
        Assert.DoesNotContain(fs.Deleted, p => IsAtOrBeneath(p, late));
        Assert.DoesNotContain(fs.Enumerated, p => IsAtOrBeneath(p, late));
        Assert.True(fs.Exists(late + "/share.txt"));
        Assert.False(fs.Exists(Instance + "/run.sh"));
    }

    [Fact]
    public void Delete_DoesNotRetryAnUnmountThatJustFailed()
    {
        var fs = InstanceTree().MountAt(Tower, device: 7, canUnmount: false).File(Sentinel);
        var deleter = Deleter(fs);

        Assert.Equal([Tower], deleter.UnmountBeneath(Instance + "/_work"));
        var result = deleter.Delete(Instance);

        Assert.Equal([Tower], fs.UnmountCalls);
        Assert.Equal([Tower], result.RemainingMounts);
        AssertUntouched(fs, Tower);
    }

    [Fact]
    public void Delete_RemovesSymlinksWithoutFollowingThem()
    {
        var link = Instance + "/_work/_temp/link-to-share";
        var fs = InstanceTree().Symlink(link).File("/Volumes/share/data.txt");

        var result = Deleter(fs).Delete(Instance);

        Assert.True(result.Completed, result.Describe());
        Assert.Contains(link, fs.Deleted);
        Assert.DoesNotContain(link, fs.Enumerated);
        Assert.True(fs.Exists("/Volumes/share/data.txt"));
    }

    [Fact]
    public void Delete_RootThatIsASymlink_RemovesOnlyTheLink()
    {
        var fs = new FakeMountFileSystem().Symlink(Instance).File("/Volumes/share/data.txt");

        var result = Deleter(fs).Delete(Instance);

        Assert.True(result.Completed);
        Assert.Equal([Instance], fs.Deleted);
        Assert.Empty(fs.Enumerated);
        Assert.Empty(fs.UnmountCalls);
    }

    [Fact]
    public void Delete_RootThatIsStillMounted_DeletesNothing()
    {
        var fs = new FakeMountFileSystem().MountAt(Instance, device: 7, canUnmount: false).File(Instance + "/data.txt");

        var result = Deleter(fs).Delete(Instance);

        Assert.False(result.Completed);
        Assert.Equal([Instance], result.RemainingMounts);
        Assert.Empty(fs.Deleted);
        Assert.Empty(fs.Enumerated);
    }

    [Fact]
    public void Delete_RootThatIsAnUnlistedMountPoint_DeletesNothing()
    {
        var fs = new FakeMountFileSystem().Directory(Instance, device: 7).File(Instance + "/data.txt");

        var result = Deleter(fs).Delete(Instance);

        Assert.False(result.Completed);
        Assert.Equal([Instance], result.SkippedForeignDevices);
        Assert.Empty(fs.Deleted);
        Assert.Empty(fs.Enumerated);
    }

    [Fact]
    public void Delete_MatchesMountsReportedThroughTheMacDataVolumeFirmlink()
    {
        var fs = InstanceTree().MountAt(Tower, device: 7, canUnmount: false, reportedAs: "/System/Volumes/Data" + Tower).File(Sentinel);

        var result = Deleter(fs).Delete(Instance);

        Assert.Equal([Tower], result.RemainingMounts);
        AssertUntouched(fs, Tower);
        Assert.True(fs.Exists(Sentinel));
    }

    [Fact]
    public void Delete_ResolvesTheRootBeforeMatchingMounts()
    {
        // /var is a symlink to /private/var, so the kernel reports /private/var/folders/...
        const string alias = "/var/folders/xy/T/inst";
        const string canonical = "/private" + alias;
        var fs = new FakeMountFileSystem()
            .File(canonical + "/run.sh")
            .MountAt(canonical + "/m", device: 7, canUnmount: false).File(canonical + "/m/sentinel.txt")
            .Alias("/var", "/private/var");

        var result = Deleter(fs).Delete(alias);

        Assert.Equal([canonical + "/m"], result.RemainingMounts);
        AssertUntouched(fs, canonical + "/m");
        Assert.False(fs.Exists(canonical + "/run.sh"));
    }

    [Fact]
    public void Delete_MatchesMountPathsCaseInsensitivelyWhenAsked()
    {
        var fs = InstanceTree().MountAt(Tower, device: 1, canUnmount: false, reportedAs: Tower.ToUpperInvariant()).File(Sentinel);

        var result = Deleter(fs, caseInsensitive: true).Delete(Instance);

        Assert.False(result.Completed);
        AssertUntouched(fs, Tower);
        Assert.True(fs.Exists(Sentinel));
    }

    [Fact]
    public void Delete_IgnoresMountsUnderASiblingWithTheSamePrefix()
    {
        var fs = InstanceTree().MountAt(Instance + "def/m", device: 7, canUnmount: false);

        var result = Deleter(fs).Delete(Instance);

        Assert.True(result.Completed, result.Describe());
        Assert.Empty(fs.UnmountCalls);
        Assert.True(fs.IsMounted(Instance + "def/m"));
    }

    [Fact]
    public void Delete_MissingDirectory_IsComplete()
    {
        var result = Deleter(new FakeMountFileSystem()).Delete(Instance);

        Assert.True(result.Completed);
    }

    [Fact]
    public void Delete_RefusesTheFilesystemRoot()
    {
        var fs = new FakeMountFileSystem().File("/etc/hosts");

        var result = Deleter(fs).Delete("/");

        Assert.False(result.Completed);
        Assert.Empty(fs.Deleted);
    }

    [Fact]
    public void UnmountBeneath_ReturnsOnlyTheMountsThatRemain()
    {
        var released = Instance + "/_work/_temp/released";
        var fs = InstanceTree()
            .MountAt(released, device: 2, canUnmount: true)
            .MountAt(Tower, device: 7, canUnmount: false);

        var remaining = Deleter(fs).UnmountBeneath(Instance);

        Assert.NotNull(remaining);
        Assert.Equal([Tower], remaining);
        Assert.False(fs.IsMounted(released));
        Assert.Empty(fs.Deleted);
    }

    [Fact]
    public void UnmountBeneath_WithoutAMountTable_ReportsThatMountsCantBeRuledOut()
    {
        var fs = InstanceTree().MountAt(Tower, device: 1, canUnmount: true);
        fs.MountTableUnavailable = true;

        Assert.Null(Deleter(fs).UnmountBeneath(Instance));
        Assert.Empty(fs.UnmountCalls);
    }

    [Fact]
    public void UnmountBeneath_MissingDirectory_HasNoMounts()
    {
        var fs = new FakeMountFileSystem();
        fs.MountTableUnavailable = true;

        Assert.Equal([], Deleter(fs).UnmountBeneath(Instance));
    }

    [Fact]
    public void ParseLinuxFdInfoMountId_ReadsTheMountId()
    {
        Assert.Equal(29, SystemMountSafeFileSystem.ParseLinuxFdInfoMountId(["pos:\t0", "flags:\t02304000", "mnt_id:\t29", "ino:\t2"]));
        Assert.Null(SystemMountSafeFileSystem.ParseLinuxFdInfoMountId(["pos:\t0", "flags:\t02304000"]));
    }

    [Fact]
    public void ParseLinuxMountInfo_ReadsAndUnescapesMountPoints()
    {
        string[] lines =
        [
            "22 1 0:21 / /proc rw,nosuid,nodev,noexec,relatime shared:5 - proc proc rw",
            "98 29 0:45 / /home/rr/.runnerrunner/work/rr-1/_temp/tower\\040share rw,relatime shared:50 - cifs //tower/appdata rw",
            "99 29 0:46 / /tmp/tab\\011and\\134slash rw - tmpfs tmpfs rw",
            "",
        ];

        var mounts = SystemMountSafeFileSystem.ParseLinuxMountInfo(lines);

        Assert.Equal(["/proc", "/home/rr/.runnerrunner/work/rr-1/_temp/tower share", "/tmp/tab\tand\\slash"], mounts);
    }

    [Fact]
    public void SystemFileSystem_ReportsTheMountTableOnUnix()
    {
        if (OperatingSystem.IsWindows())
            return;

        var mounts = SystemMountSafeFileSystem.Instance.GetMountPoints();

        Assert.Contains("/", mounts);
    }

    [Fact]
    public void SystemFileSystem_DetectsADeviceBoundaryAtAKnownMountPoint()
    {
        // /proc (Linux) and /dev (macOS devfs) are always their own filesystems, so the st_dev
        // backstop must see a boundary there.
        string? mountPoint = OperatingSystem.IsLinux() ? "/proc" : OperatingSystem.IsMacOS() ? "/dev" : null;
        if (mountPoint is null || !System.IO.Directory.Exists(mountPoint))
            return;

        var fs = SystemMountSafeFileSystem.Instance;
        using var root = fs.OpenDirectory("/");
        using var mounted = root.OpenChildDirectory(mountPoint[1..]);
        using var usr = root.OpenChildDirectory("usr");

        Assert.NotNull(mounted);
        Assert.NotNull(usr);
        Assert.NotEqual(root.DeviceId, mounted.DeviceId);
        Assert.Equal(root.DeviceId, usr.DeviceId);
        if (OperatingSystem.IsLinux())
        {
            Assert.NotNull(root.MountId);
            Assert.NotEqual(root.MountId, mounted.MountId);
        }
    }

    [Fact]
    public void SystemFileSystem_HandlesListOpenAndRemoveEntriesWithoutFollowingSymlinks()
    {
        if (OperatingSystem.IsWindows())
            return;

        var temp = CreateTempDirectory();
        var outside = CreateTempDirectory();
        try
        {
            System.IO.Directory.CreateDirectory(Path.Combine(temp, "sub dir"));
            System.IO.File.WriteAllText(Path.Combine(temp, "fichier é.txt"), "x");
            System.IO.File.WriteAllText(Path.Combine(outside, "sentinel.txt"), "outside");
            System.IO.File.CreateSymbolicLink(Path.Combine(temp, "link"), outside);

            var fs = SystemMountSafeFileSystem.Instance;
            using var directory = fs.OpenDirectory(temp);

            Assert.Equal(["fichier é.txt", "link", "sub dir"], directory.ReadEntryNames().Order(StringComparer.Ordinal));
            Assert.Null(directory.OpenChildDirectory("link"));
            Assert.Null(directory.OpenChildDirectory("fichier é.txt"));
            Assert.Null(directory.OpenChildDirectory("missing"));
            using (var sub = directory.OpenChildDirectory("sub dir"))
            {
                Assert.NotNull(sub);
                Assert.Equal(directory.DeviceId, sub.DeviceId);
                Assert.Empty(sub.ReadEntryNames());
            }

            directory.DeleteChild("link");
            directory.DeleteChild("fichier é.txt");
            directory.DeleteChild("missing");
            directory.DeleteChildDirectory("sub dir");

            Assert.Empty(directory.ReadEntryNames());
            Assert.True(System.IO.File.Exists(Path.Combine(outside, "sentinel.txt")));
            Assert.Throws<IOException>(() => fs.OpenDirectory(Path.Combine(outside, "sentinel.txt")));
        }
        finally
        {
            System.IO.Directory.Delete(temp, recursive: true);
            System.IO.Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void SystemFileSystem_DirectorySwappedForASymlinkMidWalk_IsNotFollowed()
    {
        // Reproduces the race the path-based walker lost: a directory is listed, then replaced
        // by a symlink to data outside the instance before the walk opens it.
        if (OperatingSystem.IsWindows())
            return;

        var temp = CreateTempDirectory();
        var instance = Path.Combine(temp, "instance");
        var outside = Path.Combine(temp, "tower-appdata");
        var swapped = Path.Combine(instance, "_work", "_temp", "swapme");
        System.IO.Directory.CreateDirectory(swapped);
        System.IO.File.WriteAllText(Path.Combine(swapped, "local.txt"), "local");
        System.IO.Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.txt");
        System.IO.File.WriteAllText(sentinel, "tower appdata");

        try
        {
            var swappedAt = false;
            var fs = new HookedFileSystem(SystemMountSafeFileSystem.Instance, path =>
            {
                if (swappedAt || !path.EndsWith("/swapme", StringComparison.Ordinal))
                    return;
                System.IO.Directory.Move(path, path + ".moved");
                System.IO.File.CreateSymbolicLink(path, outside);
                swappedAt = true;
            });

            Real(fs).Delete(instance);

            Assert.True(swappedAt, "the race window was never hit");
            Assert.True(System.IO.File.Exists(sentinel), "data outside the instance was deleted through a swapped-in symlink");
            Assert.False(System.IO.File.Exists(Path.Combine(instance, "_work", "_temp", "swapme")), "the swapped-in symlink should be removed");
        }
        finally
        {
            System.IO.Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void SystemFileSystem_RootSwappedForASymlinkBeforeItIsResolved_IsNotFollowed()
    {
        // The root passes the "is a directory" check, then is replaced by a symlink before it is
        // canonicalized; resolving it would aim the whole delete at the symlink's target.
        if (OperatingSystem.IsWindows())
            return;

        var temp = CreateTempDirectory();
        var instance = Path.Combine(temp, "instance");
        var outside = Path.Combine(temp, "tower-appdata");
        System.IO.Directory.CreateDirectory(Path.Combine(instance, "_work"));
        System.IO.Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.txt");
        System.IO.File.WriteAllText(sentinel, "tower appdata");

        try
        {
            var swapped = false;
            // Swap right after the root's own "is a directory" check.
            var fs = new KindHookFileSystem(SystemMountSafeFileSystem.Instance, p =>
            {
                if (swapped || p != instance)
                    return;
                System.IO.Directory.Move(instance, instance + ".moved");
                System.IO.File.CreateSymbolicLink(instance, outside);
                swapped = true;
            });

            var result = Real(fs).Delete(instance);

            Assert.True(swapped, "the race window was never hit");
            Assert.True(System.IO.File.Exists(sentinel), "data outside the instance was deleted through a root swapped for a symlink");
            Assert.False(result.Completed);
        }
        finally
        {
            System.IO.Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void SystemFileSystem_AncestorSwappedForASymlinkBeforeItWasResolved_IsNotFollowed()
    {
        // The swap lands before resolution, so a realpath of the parent would follow it to the
        // other tree and delete the same-named directory there.
        if (OperatingSystem.IsWindows())
            return;

        var temp = CreateTempDirectory();
        var host = Path.Combine(temp, "host");
        var instance = Path.Combine(host, "instances", "inst");
        var outside = Path.Combine(temp, "tower");
        System.IO.Directory.CreateDirectory(Path.Combine(instance, "_work"));
        System.IO.Directory.CreateDirectory(Path.Combine(outside, "instances", "inst"));
        var sentinel = Path.Combine(outside, "instances", "inst", "sentinel.txt");
        System.IO.File.WriteAllText(sentinel, "tower appdata");

        try
        {
            var swapped = false;
            // Swap right after the root's own kind check, before any ancestor is resolved.
            var hooked = new KindHookFileSystem(SystemMountSafeFileSystem.Instance, p =>
            {
                if (swapped || p != instance)
                    return;
                System.IO.Directory.Move(host, host + ".moved");
                System.IO.File.CreateSymbolicLink(host, outside);
                swapped = true;
            });

            var result = Real(hooked).Delete(instance);

            Assert.True(swapped, "the race window was never hit");
            Assert.True(System.IO.File.Exists(sentinel), "data outside the instance was deleted through an ancestor swapped for a symlink");
            Assert.False(result.Completed);
        }
        finally
        {
            System.IO.Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void Delete_ThroughASystemSymlinkAncestor_StillWorks()
    {
        var fs = new FakeMountFileSystem()
            .File("/private/var/folders/x/inst/run.sh")
            .Alias("/var", "/private/var");

        var result = Deleter(fs).Delete("/var/folders/x/inst");

        Assert.True(result.Completed, result.Describe());
        Assert.False(fs.Exists("/private/var/folders/x/inst"));
    }

    [Fact]
    public void Delete_AsRoot_FollowsNoSymlinkAncestorAtAll()
    {
        var fs = new FakeMountFileSystem()
            .File("/private/var/folders/x/inst/run.sh")
            .Alias("/var", "/private/var");

        var result = new MountSafeDirectoryDeleter(_logger, fs, TimeSpan.FromSeconds(1), runningAsRoot: true).Delete("/var/folders/x/inst");

        Assert.False(result.Completed);
        Assert.True(fs.Exists("/private/var/folders/x/inst/run.sh"));
    }

    [Fact]
    public void Delete_ThroughAnAncestorSymlinkBelowTheRoot_DeletesNothing()
    {
        var fs = new FakeMountFileSystem()
            .File("/Volumes/Tower/data/instances/inst/sentinel.txt")
            .Alias("/Users/runner/.runnerrunner", "/Volumes/Tower/data");

        var result = Deleter(fs).Delete("/Users/runner/.runnerrunner/instances/inst");

        Assert.False(result.Completed);
        Assert.True(fs.Exists("/Volumes/Tower/data/instances/inst/sentinel.txt"));
        Assert.Empty(fs.Deleted);
    }

    [Fact]
    public void SystemFileSystem_AncestorSwappedForASymlinkAfterItWasResolved_IsNotFollowed()
    {
        // After the path is resolved, a directory above the root is replaced by a symlink to
        // another tree with the same layout; opening the parent by path would land in that tree.
        if (OperatingSystem.IsWindows())
            return;

        var temp = CreateTempDirectory();
        var host = Path.Combine(temp, "host");
        var instance = Path.Combine(host, "instances", "inst");
        var outside = Path.Combine(temp, "tower");
        System.IO.Directory.CreateDirectory(Path.Combine(instance, "_work"));
        System.IO.Directory.CreateDirectory(Path.Combine(outside, "instances", "inst"));
        var sentinel = Path.Combine(outside, "instances", "inst", "sentinel.txt");
        System.IO.File.WriteAllText(sentinel, "tower appdata");

        try
        {
            var swapped = false;
            // Swap after "host" was checked as a real directory, while later ancestors are resolved.
            var fs = new KindHookFileSystem(SystemMountSafeFileSystem.Instance, p =>
            {
                if (swapped || p != Path.Combine(host, "instances"))
                    return;
                System.IO.Directory.Move(host, host + ".moved");
                System.IO.File.CreateSymbolicLink(host, outside);
                swapped = true;
            });

            var result = Real(fs).Delete(instance);

            Assert.True(swapped, "the race window was never hit");
            Assert.True(System.IO.File.Exists(sentinel), "data outside the instance was deleted through an ancestor swapped for a symlink");
            Assert.False(result.Completed);
        }
        finally
        {
            System.IO.Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void SystemFileSystem_DiskImageMountedAfterTheMountTableWasRead_IsNotEntered()
    {
        if (!MacDiskImage.IsSupported)
            return;

        var temp = CreateTempDirectory();
        var instance = Path.Combine(temp, "instance");
        var late = Path.Combine(instance, "_work", "_temp", "late");
        System.IO.Directory.CreateDirectory(late);
        System.IO.File.WriteAllText(Path.Combine(instance, "run.sh"), "#!/bin/sh");
        using var image = MacDiskImage.Create(Path.Combine(temp, "late.dmg"), "rrlate");

        try
        {
            var fs = new HookedFileSystem(SystemMountSafeFileSystem.Instance, path =>
            {
                if (image.MountPoint is not null || !path.EndsWith("/late", StringComparison.Ordinal))
                    return;
                image.Attach(late);
                System.IO.File.WriteAllText(Path.Combine(late, "sentinel.txt"), "tower appdata");
            });

            var result = Real(fs).Delete(instance);

            Assert.True(image.IsMounted(), "the late mount should still be mounted");
            Assert.Equal("tower appdata", image.ReadFromImage("sentinel.txt", late));
            Assert.False(result.Completed);
            Assert.Single(result.SkippedForeignDevices);
            Assert.False(System.IO.File.Exists(Path.Combine(instance, "run.sh")));
        }
        finally
        {
            image.Dispose();
            System.IO.Directory.Delete(temp, recursive: true);
        }
    }

    private MountSafeDirectoryDeleter Real(IMountSafeFileSystem fs)
        => new(_logger, fs, TimeSpan.FromSeconds(10), OperatingSystem.IsMacOS());

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "rr-mount-safety-" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(path);
        return SystemMountSafeFileSystem.Instance.GetCanonicalPath(path);
    }

    /// <summary>Runs an action after each entry-kind check.</summary>
    private sealed class KindHookFileSystem(IMountSafeFileSystem inner, Action<string> afterKind) : IMountSafeFileSystem
    {
        public bool SupportsMountDetection => inner.SupportsMountDetection;
        public string GetCanonicalPath(string path) => inner.GetCanonicalPath(path);
        public IReadOnlyList<string> GetMountPoints() => inner.GetMountPoints();
        public bool TryUnmount(string mountPoint, TimeSpan timeout, out string? error) => inner.TryUnmount(mountPoint, timeout, out error);
        public void DeleteFile(string path) => inner.DeleteFile(path);
        public IMountSafeDirectoryHandle OpenDirectory(string path) => inner.OpenDirectory(path);

        public FileSystemEntryKind GetEntryKind(string path)
        {
            var kind = inner.GetEntryKind(path);
            afterKind(path);
            return kind;
        }
    }

    /// <summary>Wraps a filesystem to run actions around canonicalization and before each child directory is opened.</summary>
    private sealed class HookedFileSystem(
        IMountSafeFileSystem inner,
        Action<string> beforeOpenChild,
        Action<string>? beforeCanonicalize = null,
        Action<string>? afterCanonicalize = null) : IMountSafeFileSystem
    {
        public bool SupportsMountDetection => inner.SupportsMountDetection;

        public string GetCanonicalPath(string path)
        {
            beforeCanonicalize?.Invoke(path);
            var canonical = inner.GetCanonicalPath(path);
            afterCanonicalize?.Invoke(canonical);
            return canonical;
        }

        public IReadOnlyList<string> GetMountPoints() => inner.GetMountPoints();
        public bool TryUnmount(string mountPoint, TimeSpan timeout, out string? error) => inner.TryUnmount(mountPoint, timeout, out error);
        public FileSystemEntryKind GetEntryKind(string path) => inner.GetEntryKind(path);
        public void DeleteFile(string path) => inner.DeleteFile(path);
        public IMountSafeDirectoryHandle OpenDirectory(string path) => new Handle(inner.OpenDirectory(path), beforeOpenChild);

        private sealed class Handle(IMountSafeDirectoryHandle inner, Action<string> beforeOpenChild) : IMountSafeDirectoryHandle
        {
            public string Path => inner.Path;
            public ulong DeviceId => inner.DeviceId;
            public long? MountId => inner.MountId;
            public IReadOnlyList<string> ReadEntryNames() => inner.ReadEntryNames();
            public void DeleteChild(string name) => inner.DeleteChild(name);
            public void DeleteChildDirectory(string name) => inner.DeleteChildDirectory(name);
            public void Dispose() => inner.Dispose();

            public IMountSafeDirectoryHandle? OpenChildDirectory(string name)
            {
                beforeOpenChild(inner.Path + "/" + name);
                var child = inner.OpenChildDirectory(name);
                return child is null ? null : new Handle(child, beforeOpenChild);
            }
        }
    }

    private MountSafeDirectoryDeleter Deleter(FakeMountFileSystem fs, bool caseInsensitive = false)
    {
        _fakes.Add(fs);
        return new(_logger, fs, TimeSpan.FromSeconds(1), caseInsensitive, runningAsRoot: false);
    }

    private static FakeMountFileSystem InstanceTree()
        => new FakeMountFileSystem()
            .File(Instance + "/run.sh")
            .File(Instance + "/bin/Runner.Listener")
            .File(Instance + "/_work/repo/src/main.swift")
            .Directory(Instance + "/_work/_temp/visionos.zGvzSG");

    private static void AssertUntouched(FakeMountFileSystem fs, string mountPoint)
    {
        Assert.True(fs.IsMounted(mountPoint), $"{mountPoint} should still be mounted");
        Assert.DoesNotContain(fs.Deleted, p => IsAtOrBeneath(p, mountPoint));
        Assert.DoesNotContain(fs.Enumerated, p => IsAtOrBeneath(p, mountPoint));
        Assert.DoesNotContain(fs.Inspected, p => IsAtOrBeneath(p, mountPoint));
    }

    private static bool IsAtOrBeneath(string path, string root)
        => path == root || path.StartsWith(root + "/", StringComparison.Ordinal);
}
