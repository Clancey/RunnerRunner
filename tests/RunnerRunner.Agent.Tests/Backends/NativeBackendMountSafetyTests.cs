using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RunnerRunner.Agent.Backends;
using RunnerRunner.Agent.Tests.TestSupport;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Agent.Tests.Backends;

/// <summary>
/// Reproduces the 2026-10-20 incident: a job mounted a network share inside its native
/// instance directory (<c>_work/_temp/visionos.*/tower.*</c>), the job was cancelled, and the
/// orphan cleanup's recursive delete walked into the live mount and deleted the share's data.
/// A disk image stands in for the share: it is a real, separate filesystem at the same depth.
/// </summary>
public class NativeBackendMountSafetyTests
{
    private const string SentinelRelativePath = "appdata/sentinel.txt";
    private const string SentinelContents = "tower appdata must survive runner cleanup";

    [Fact]
    public async Task CleanupOrphanProcess_DoesNotDeleteThroughDiskImageMountedInsideInstanceDir()
    {
        if (!MacDiskImage.IsSupported)
            return;

        var root = Directory.CreateTempSubdirectory("rr-mount-safety-");
        var instanceDir = CreateInstanceDir(root.FullName);
        var mountPoint = Path.Combine(instanceDir, "_work", "_temp", "visionos.zGvzSG", "tower.9tkeMx");
        var image = MacDiskImage.Create(Path.Combine(root.FullName, "tower.dmg"), "rrtower");
        using var orphan = StartOrphanRunnerProcess();
        try
        {
            image.Attach(mountPoint);
            WriteSentinel(mountPoint);

            var backend = new NativeBackend(new ListLogger<NativeBackend>());
            await backend.CleanupOrphanProcessAsync(orphan.Id, instanceDir);

            Assert.Equal(SentinelContents, image.ReadFromImage(SentinelRelativePath, Path.Combine(root.FullName, "verify-mount")));
            // The mount was ours to release, so after a clean unmount nothing is left behind.
            Assert.False(Directory.Exists(instanceDir), "instance directory should be removed once the nested mount was unmounted");
        }
        finally
        {
            Cleanup(orphan, image, root);
        }
    }

    [Fact]
    public async Task CleanupOrphanProcess_LeavesBusyMountAndItsDataInPlaceButRemovesTheRest()
    {
        if (!MacDiskImage.IsSupported)
            return;

        var root = Directory.CreateTempSubdirectory("rr-mount-safety-");
        var instanceDir = CreateInstanceDir(root.FullName);
        var mountPoint = Path.Combine(instanceDir, "_work", "_temp", "visionos.zGvzSG", "tower.9tkeMx");
        var image = MacDiskImage.Create(Path.Combine(root.FullName, "tower.dmg"), "rrtower");
        using var orphan = StartOrphanRunnerProcess();
        try
        {
            image.Attach(mountPoint);
            WriteSentinel(mountPoint);

            var logger = new ListLogger<NativeBackend>();
            var backend = new NativeBackend(logger);

            // An open file keeps a normal (non-forced) unmount from succeeding, like a share a
            // stuck process is still using.
            using (File.Open(Path.Combine(mountPoint, "appdata", "busy.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                await backend.CleanupOrphanProcessAsync(orphan.Id, instanceDir);

                Assert.True(image.IsMounted(), "busy mount must not be force-unmounted");
                Assert.Equal(SentinelContents, File.ReadAllText(Path.Combine(mountPoint, SentinelRelativePath)));
            }

            Assert.False(File.Exists(Path.Combine(instanceDir, "run.sh")), "runner files outside the mount should still be cleaned up");
            Assert.False(Directory.Exists(Path.Combine(instanceDir, "_work", "repo")), "work files outside the mount should still be cleaned up");
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("tower.9tkeMx", StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(orphan, image, root);
        }
    }

    [Fact]
    public async Task PostExitCleanupStep_IsSkippedWhileAJobMountIsStillInsideTheWorkDir()
    {
        if (!MacDiskImage.IsSupported)
            return;

        var root = Directory.CreateTempSubdirectory("rr-mount-safety-");
        var instanceDir = CreateInstanceDir(root.FullName);
        var workDir = CreateWorkDir(root.FullName);
        var mountPoint = Path.Combine(workDir, "_temp", "visionos.zGvzSG", "tower.9tkeMx");
        var image = MacDiskImage.Create(Path.Combine(root.FullName, "tower.dmg"), "rrtower");
        try
        {
            image.Attach(mountPoint);
            WriteSentinel(mountPoint);

            var logger = new ListLogger<NativeBackend>();
            var backend = new NativeBackend(logger);

            using (File.Open(Path.Combine(mountPoint, "appdata", "busy.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                var ran = await backend.RunPostExitStepsAsync(
                    "runner", workDir, instanceDir, [WorkDirCleanupStep()], WorkDirEnvironment(workDir), null, CancellationToken.None);

                Assert.True(File.Exists(Path.Combine(mountPoint, SentinelRelativePath)), "sentinel inside the mounted image was deleted");
                Assert.Equal(SentinelContents, File.ReadAllText(Path.Combine(mountPoint, SentinelRelativePath)));
                Assert.True(image.IsMounted(), "busy mount must not be force-unmounted");
                Assert.False(ran, "post-exit steps must not run while a mount remains inside the work dir");
            }

            Assert.True(File.Exists(Path.Combine(workDir, "repo", "README.md")), "skipped step must not have touched the work dir");
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("tower.9tkeMx", StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(null, image, root);
        }
    }

    [Fact]
    public async Task PostExitCleanupStep_RunsAfterUnmountingAJobMountInsideTheWorkDir()
    {
        if (!MacDiskImage.IsSupported)
            return;

        var root = Directory.CreateTempSubdirectory("rr-mount-safety-");
        var instanceDir = CreateInstanceDir(root.FullName);
        var workDir = CreateWorkDir(root.FullName);
        var mountPoint = Path.Combine(workDir, "_temp", "visionos.zGvzSG", "tower.9tkeMx");
        var image = MacDiskImage.Create(Path.Combine(root.FullName, "tower.dmg"), "rrtower");
        try
        {
            image.Attach(mountPoint);
            WriteSentinel(mountPoint);

            var backend = new NativeBackend(new ListLogger<NativeBackend>());
            var ran = await backend.RunPostExitStepsAsync(
                "runner", workDir, instanceDir, [WorkDirCleanupStep()], WorkDirEnvironment(workDir), null, CancellationToken.None);

            Assert.True(ran);
            Assert.False(Directory.Exists(workDir), "the cleanup step should have removed the work dir");
            Assert.Equal(SentinelContents, image.ReadFromImage(SentinelRelativePath, Path.Combine(root.FullName, "verify-mount")));
        }
        finally
        {
            Cleanup(null, image, root);
        }
    }

    // Mirrors the "[init:Cleanup work directory]" post-exit step used on real hosts.
    private static ResolvedInitStep WorkDirCleanupStep() => new()
    {
        Name = "Cleanup work directory",
        Phase = InitStepPhase.PostExit,
        Shell = InitStepShell.Bash,
        Script = "rm -rf \"$RR_WORK_DIR\"",
        TimeoutSeconds = 60
    };

    private static Dictionary<string, string> WorkDirEnvironment(string workDir) => new()
    {
        ["RR_WORK_DIR"] = workDir
    };

    private static string CreateWorkDir(string root)
    {
        var workDir = Path.Combine(root, "work", "rr-53d8adc8282263fc");
        Directory.CreateDirectory(Path.Combine(workDir, "repo"));
        File.WriteAllText(Path.Combine(workDir, "repo", "README.md"), "checkout");
        return workDir;
    }

    private static string CreateInstanceDir(string root)
    {
        var instanceDir = Path.Combine(root, "instances", "rr-53d8adc8282263fc");
        Directory.CreateDirectory(Path.Combine(instanceDir, "bin"));
        File.WriteAllText(Path.Combine(instanceDir, "run.sh"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(instanceDir, "bin", "Runner.Listener"), "binary");
        Directory.CreateDirectory(Path.Combine(instanceDir, "_work", "repo", "src"));
        File.WriteAllText(Path.Combine(instanceDir, "_work", "repo", "src", "main.swift"), "print(1)");
        return instanceDir;
    }

    private static void WriteSentinel(string mountPoint)
    {
        Directory.CreateDirectory(Path.Combine(mountPoint, "appdata"));
        File.WriteAllText(Path.Combine(mountPoint, SentinelRelativePath), SentinelContents);
    }

    private static Process StartOrphanRunnerProcess()
        => Process.Start(new ProcessStartInfo("/bin/sleep", "300") { UseShellExecute = false })
           ?? throw new InvalidOperationException("Failed to start stand-in runner process");

    private static void Cleanup(Process? orphan, MacDiskImage image, DirectoryInfo root)
    {
        try
        {
            if (orphan is { HasExited: false })
                orphan.Kill();
        }
        catch (InvalidOperationException)
        {
        }

        // Detach before deleting the temp root so test cleanup never walks into the image.
        image.Dispose();
        try
        {
            root.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
