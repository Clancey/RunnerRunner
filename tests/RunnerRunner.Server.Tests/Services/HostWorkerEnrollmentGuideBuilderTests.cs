using Microsoft.Extensions.Configuration;
using RunnerRunner.Agent.Tests.TestSupport;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services.HostWorkers;

namespace RunnerRunner.Server.Tests.Services;

public sealed class HostWorkerEnrollmentGuideBuilderTests
{
    [Fact]
    public void ResolveServerUrl_UsesConfiguredPublicUrl()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["HostWorkerEnrollment:PublicServerUrl"] = "https://runner.example.com/hostworker"
        });

        var serverUrl = builder.ResolveServerUrl("http://localhost:4779/");

        Assert.Equal("https://runner.example.com/hostworker", serverUrl);
    }

    [Fact]
    public void ResolveServerUrl_DerivesGrpcPortFromDefaultUiPort()
    {
        var builder = CreateBuilder();

        var serverUrl = builder.ResolveServerUrl("http://192.168.2.4:4779/");

        Assert.Equal("http://192.168.2.4:4780", serverUrl);
    }

    [Fact]
    public void ResolveServerUrl_UsesOrleansAdvertisedIpWhenPublicUrlIsNotConfigured()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Orleans:AdvertisedIPAddress"] = "192.168.2.4",
            ["Kestrel:Endpoints:HostWorkerGrpc:Url"] = "http://+:4780"
        });

        var serverUrl = builder.ResolveServerUrl("http://localhost:4779/");

        Assert.Equal("http://192.168.2.4:4780", serverUrl);
    }

    [Fact]
    public void ResolveServerUrl_UsesConfiguredAdvertisedPortWithOrleansIp()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["Orleans:AdvertisedIPAddress"] = "192.168.2.4",
            ["HostWorkerEnrollment:PublicServerScheme"] = "https",
            ["HostWorkerEnrollment:PublicServerPort"] = "9443"
        });

        var serverUrl = builder.ResolveServerUrl("http://localhost:4779/");

        Assert.Equal("https://192.168.2.4:9443", serverUrl);
    }

    [Fact]
    public void BuildLinuxDocker_IncludesConnectionArgumentsAndProxy()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["HostWorkerEnrollment:HostWorkerImage"] = "ghcr.io/example/worker:test"
        });

        var instructions = builder.Build(new HostWorkerEnrollmentRequest(
            HostWorkerEnrollmentTarget.LinuxDocker,
            "http://runner.example.com:4780",
            "token-123",
            "linux-host-1",
            "Linux Host 1",
            new HostWorkerEnrollmentProxy(
                "http://proxy:8080",
                "http://secure-proxy:8080",
                "localhost,127.0.0.1")));

        var command = Assert.Single(instructions.CommandBlocks).Command;
        Assert.Contains("ghcr.io/example/worker:test", command);
        Assert.Contains("HostWorker__ServerUrl: 'http://runner.example.com:4780'", command);
        Assert.Contains("HostWorker__EnrollmentToken: 'token-123'", command);
        Assert.Contains("HostWorker__HostId: 'linux-host-1'", command);
        Assert.Contains("HTTP_PROXY: 'http://proxy:8080'", command);
        Assert.Contains("HTTPS_PROXY: 'http://secure-proxy:8080'", command);
        Assert.Contains("NO_PROXY: 'localhost,127.0.0.1'", command);
    }

    [Fact]
    public void BuildWindowsService_ProducesPowerShellAutoSetup()
    {
        var builder = CreateBuilder();

        var instructions = builder.Build(new HostWorkerEnrollmentRequest(
            HostWorkerEnrollmentTarget.WindowsService,
            "http://runner.example.com:4780",
            "token-123",
            "windows-host-1",
            "Windows Host 1",
            new HostWorkerEnrollmentProxy(null, null, null)));

        Assert.Equal(HostWorkerEnrollmentRemoteShell.PowerShell, instructions.RemoteShell);
        Assert.Contains("Install-HostWorker.ps1", instructions.RemoteSetupScript);
        Assert.Contains("-ServerUrl 'http://runner.example.com:4780'", instructions.RemoteSetupScript);
        Assert.Contains("-EnrollmentToken 'token-123'", instructions.RemoteSetupScript);
    }

    [Fact]
    public void BuildWindowsDockerWindows_UsesSlashSafeDataMountTarget()
    {
        var builder = CreateBuilder();

        var instructions = builder.Build(new HostWorkerEnrollmentRequest(
            HostWorkerEnrollmentTarget.WindowsDockerWindows,
            "http://runner.example.com:4780",
            "token-123",
            "windows-docker-1",
            "Windows Docker 1",
            new HostWorkerEnrollmentProxy(null, null, null)));

        var command = Assert.Single(instructions.CommandBlocks).Command;
        Assert.Contains("target=C:/ProgramData/RunnerRunner", command);
        Assert.DoesNotContain(@"target=C:\ProgramData\RunnerRunner", command);
    }

    [Fact]
    public void BuildWindowsDockerLinux_ProvidesWsl2AndArm64Onboarding()
    {
        var builder = CreateBuilder();

        var instructions = builder.Build(new HostWorkerEnrollmentRequest(
            HostWorkerEnrollmentTarget.WindowsDockerLinux,
            "http://runner.example.com:4780",
            "token-123",
            "windows-wsl2-1",
            "Windows WSL2 1",
            new HostWorkerEnrollmentProxy(null, null, null)));

        Assert.Equal(HostPlatform.Linux, instructions.HostPlatform);
        Assert.Equal(3, instructions.CommandBlocks.Count);
        Assert.Contains("wsl --set-default-version 2", instructions.CommandBlocks[0].Command);
        Assert.Contains("wsl --install -d Ubuntu", instructions.CommandBlocks[0].Command);
        Assert.Contains("docker info --format '{{.OSType}}'", instructions.CommandBlocks[1].Command);
        Assert.Contains("ghcr.io/redth/runnerrunner-hostworker:latest", instructions.CommandBlocks[2].Command);
        Assert.Contains(instructions.Notes, note => note.Contains("downloads ARM64 runner agents automatically"));
        Assert.Contains(instructions.SetupLinks!, link => link.Title == "Install WSL");
        Assert.Contains(instructions.SetupLinks!, link => link.Title == "Docker Desktop WSL2 backend");
    }

    [Fact]
    public void BuildManualUpdate_ForMacOSNative_PreservesExistingConfig()
    {
        var builder = CreateBuilder();

        var instructions = builder.BuildManualUpdate(new HostWorkerManualUpdateRequest(
            HostWorkerEnrollmentTarget.MacOSNative,
            "host-1",
            "Mac Mini",
            "mac-mini",
            null,
            new HostWorkerEnrollmentProxy(null, null, null)));

        var command = Assert.Single(instructions.CommandBlocks).Command;
        Assert.Equal(HostWorkerEnrollmentRemoteShell.Bash, instructions.RemoteShell);
        Assert.Contains("--preserve-config", command);
        Assert.DoesNotContain("--enrollment-token", command);
    }

    [Fact]
    public void BuildManualUpdate_ForMacOSNative_UsesSelectedAsset()
    {
        var builder = CreateBuilder();

        var instructions = builder.BuildManualUpdate(new HostWorkerManualUpdateRequest(
            HostWorkerEnrollmentTarget.MacOSNative,
            "host-1",
            "Mac Mini",
            "mac-mini",
            null,
            new HostWorkerEnrollmentProxy(null, null, null),
            new HostWorkerManualUpdatePackage(
                HostWorkerUpdateSourceKind.Release,
                "main",
                "abc123def456",
                "runnerrunner-hostworker-osx-arm64.tar.gz",
                "https://runner.example.test/asset.tar.gz?sha256=abc",
                "abc",
                null)));

        var command = Assert.Single(instructions.CommandBlocks).Command;
        Assert.Contains("version='abc123def456'", command);
        Assert.Contains("asset_url='https://runner.example.test/asset.tar.gz?sha256=abc'", command);
        Assert.Contains("expected_sha256='abc'", command);
        Assert.Contains("set -euo pipefail", command);
        Assert.Contains("Authorization: Bearer ${enrollment_token}", command);
        Assert.Contains("if ! curl \"${curl_args[@]}\"", command);
        Assert.Contains("Failed to download HostWorker update asset from ${asset_url}", command);
        Assert.Contains("tar -xzf", command);
        Assert.Contains("# --- mount-safe removal helpers ---", command);
        Assert.Contains("rr_safe_rm \"${version_dir}\"", command);
        Assert.Contains("rm -rfx \"$root\"", command);
        Assert.DoesNotContain("rm -rf \"${version_dir}\"", command);
        Assert.DoesNotContain("rm -rfx \"${version_dir}\"", command);
        Assert.DoesNotContain("install-host-macos.sh", command);
    }

    [Fact]
    public void BuildManualUpdate_ForWindowsService_UsesSelectedAssetWithExistingToken()
    {
        var builder = CreateBuilder();

        var instructions = builder.BuildManualUpdate(new HostWorkerManualUpdateRequest(
            HostWorkerEnrollmentTarget.WindowsService,
            "host-1",
            "Windows Host",
            "windows-host",
            null,
            new HostWorkerEnrollmentProxy(null, null, null),
            new HostWorkerManualUpdatePackage(
                HostWorkerUpdateSourceKind.Release,
                "main",
                "abc123def456",
                "runnerrunner-hostworker-win-x64.zip",
                "https://runner.example.test/asset.zip?sha256=abc",
                "abc",
                null)));

        var command = Assert.Single(instructions.CommandBlocks).Command;
        Assert.Equal(HostWorkerEnrollmentRemoteShell.PowerShell, instructions.RemoteShell);
        Assert.Contains("$downloadHeaders", command);
        Assert.Contains("Authorization", command);
        Assert.Contains("Invoke-WebRequest $assetUrl -OutFile $archive -Headers $downloadHeaders", command);
        Assert.Contains("Failed to download HostWorker update asset", command);
        Assert.Contains("Expand-Archive $archive", command);
    }

    [Fact]
    public void BuildManualUpdate_ForDocker_PreservesExistingContainerIdentity()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["HostWorkerEnrollment:HostWorkerImage"] = "ghcr.io/example/worker:test"
        });

        var instructions = builder.BuildManualUpdate(new HostWorkerManualUpdateRequest(
            HostWorkerEnrollmentTarget.LinuxDocker,
            "host-1",
            "Linux Host",
            "worker-1",
            "abcdef123456",
            new HostWorkerEnrollmentProxy(null, null, null)));

        var command = Assert.Single(instructions.CommandBlocks).Command;
        Assert.Contains("container='abcdef123456'", command);
        Assert.Contains("image='ghcr.io/example/worker:test'", command);
        Assert.Contains("HostWorker__EnrollmentToken=$enrollment_token", command);
        Assert.Contains("HostWorker__ServerUrl=$server_url", command);
        Assert.Contains("docker pull \"$image\"", command);
    }

    [Fact]
    public void BuildManualUpdate_ForDocker_UsesSelectedImage()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            ["HostWorkerEnrollment:HostWorkerImage"] = "ghcr.io/example/worker:latest"
        });

        var instructions = builder.BuildManualUpdate(new HostWorkerManualUpdateRequest(
            HostWorkerEnrollmentTarget.LinuxDocker,
            "host-1",
            "Linux Host",
            "worker-1",
            "abcdef123456",
            new HostWorkerEnrollmentProxy(null, null, null),
            new HostWorkerManualUpdatePackage(
                HostWorkerUpdateSourceKind.Release,
                "main",
                "abc123def456",
                null,
                null,
                null,
                "ghcr.io/example/worker:abc123def456")));

        var command = Assert.Single(instructions.CommandBlocks).Command;
        Assert.Contains("image='ghcr.io/example/worker:abc123def456'", command);
        Assert.DoesNotContain("image='ghcr.io/example/worker:latest'", command);
    }

    [Fact]
    public void BuildManualUpdate_ForWindowsDockerWindows_UsesSlashSafeDataMountTarget()
    {
        var builder = CreateBuilder();

        var instructions = builder.BuildManualUpdate(new HostWorkerManualUpdateRequest(
            HostWorkerEnrollmentTarget.WindowsDockerWindows,
            "host-1",
            "Windows Docker",
            "windows-docker",
            "abcdef123456",
            new HostWorkerEnrollmentProxy(null, null, null)));

        var command = Assert.Single(instructions.CommandBlocks).Command;
        Assert.Contains("target=C:/ProgramData/RunnerRunner", command);
        Assert.DoesNotContain(@"target=C:\ProgramData\RunnerRunner", command);
    }

    [Fact]
    public void BuildRemoval_ForWindowsService_RemovesServiceAndData()
    {
        var builder = CreateBuilder();

        var instructions = builder.BuildRemoval(new HostWorkerRemovalRequest(
            HostWorkerEnrollmentTarget.WindowsService,
            "host-1",
            "Windows Host",
            "windows-host",
            null,
            @"C:\rr",
            @"C:\rr\work"));

        var command = Assert.Single(instructions.CommandBlocks).Command;
        Assert.Equal(HostWorkerEnrollmentRemoteShell.PowerShell, instructions.RemoteShell);
        Assert.Contains("sc.exe delete $serviceName", command);
        Assert.Contains("Stop-Process -Id $runnerProcessId", command);
        Assert.Contains("rr.pid", command);
        Assert.Contains(@"C:\Program Files\RunnerRunner", command);
        Assert.Contains(@"C:\ProgramData\RunnerRunner", command);
        Assert.Contains(@"C:\rr", command);
    }

    [Fact]
    public void BuildRemoval_ForMacOSNative_UnloadsLaunchAgent()
    {
        var builder = CreateBuilder();

        var instructions = builder.BuildRemoval(new HostWorkerRemovalRequest(
            HostWorkerEnrollmentTarget.MacOSNative,
            "host-1",
            "Mac Host",
            "mac-host",
            null,
            "/Users/runner/.runnerrunner",
            "/Users/runner/.runnerrunner/work"));

        var command = Assert.Single(instructions.CommandBlocks).Command;
        Assert.Equal(HostWorkerEnrollmentRemoteShell.Bash, instructions.RemoteShell);
        Assert.Contains("launchctl bootout", command);
        Assert.Contains("kill \"$pid\"", command);
        Assert.Contains("rr.pid", command);
        Assert.Contains("com.runnerrunner.hostworker", command);
        Assert.Contains("rr_safe_rm \"${install_root}\"", command);
        Assert.Contains("rr_safe_rm \"$work_directory\"", command);
        Assert.Contains("rr_safe_rm \"$runner_base_path\"", command);
        Assert.Contains("rm -rfx \"$root\"", command);
        Assert.Contains("stat -f %d \"$root\"", command);
        Assert.Contains("kill -0 \"$pid\"", command);
        Assert.Contains("runner processes${alive} are still running", command);
        Assert.DoesNotContain("rm -rf ", command);
        Assert.DoesNotContain("__MOUNT_SAFE_REMOVAL_HELPERS__", command);
    }

    [Fact]
    public async Task MacOSNativeRemoval_RefusesToDeleteThroughABusyMount()
    {
        if (!MacDiskImage.IsSupported)
            return;

        var root = Directory.CreateTempSubdirectory("rr-removal-mount-");
        var workDir = CreateRemovalWorkTree(root.FullName);
        var mountPoint = Path.Combine(workDir, "rr-53d8adc8282263fc", "_temp", "visionos.zGvzSG", "tower 9tkeMx");
        var image = MacDiskImage.Create(Path.Combine(root.FullName, "tower.dmg"), "rrtower");
        try
        {
            image.Attach(mountPoint);
            Directory.CreateDirectory(Path.Combine(mountPoint, "appdata"));
            File.WriteAllText(Path.Combine(mountPoint, "appdata", "sentinel.txt"), "tower appdata");

            int exitCode;
            string stderr;
            using (File.Open(Path.Combine(mountPoint, "appdata", "busy.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                (exitCode, stderr) = await RunRemovalHelperAsync(workDir);
                Assert.True(File.Exists(Path.Combine(mountPoint, "appdata", "sentinel.txt")), "sentinel inside the mounted image was deleted");
            }

            Assert.NotEqual(0, exitCode);
            Assert.True(image.IsMounted(), "busy mount must not be force-unmounted");
            Assert.Contains("tower 9tkeMx", stderr);
            Assert.True(File.Exists(Path.Combine(workDir, "rr-53d8adc8282263fc", "repo", "README.md")), "refusal must leave the directory alone");
        }
        finally
        {
            image.Dispose();
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task MacOSNativeRemoval_UnmountsAJobMountThenRemovesTheDirectory()
    {
        if (!MacDiskImage.IsSupported)
            return;

        var root = Directory.CreateTempSubdirectory("rr-removal-mount-");
        var workDir = CreateRemovalWorkTree(root.FullName);
        var mountPoint = Path.Combine(workDir, "rr-53d8adc8282263fc", "_temp", "visionos.zGvzSG", "tower 9tkeMx");
        var image = MacDiskImage.Create(Path.Combine(root.FullName, "tower.dmg"), "rrtower");
        try
        {
            image.Attach(mountPoint);
            Directory.CreateDirectory(Path.Combine(mountPoint, "appdata"));
            File.WriteAllText(Path.Combine(mountPoint, "appdata", "sentinel.txt"), "tower appdata");

            var (exitCode, stderr) = await RunRemovalHelperAsync(workDir);

            Assert.Equal("tower appdata", image.ReadFromImage("appdata/sentinel.txt", Path.Combine(root.FullName, "verify-mount")));
            Assert.True(exitCode == 0, stderr);
            Assert.False(Directory.Exists(workDir));
        }
        finally
        {
            image.Dispose();
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task MacOSNativeRemoval_TargetThatIsItselfABusyMount_IsRefused()
    {
        if (!MacDiskImage.IsSupported)
            return;

        var root = Directory.CreateTempSubdirectory("rr-removal-mount-");
        var target = Path.Combine(root.FullName, "versions", "1.2.3");
        var image = MacDiskImage.Create(Path.Combine(root.FullName, "tower.dmg"), "rrtower");
        try
        {
            image.Attach(target);
            File.WriteAllText(Path.Combine(target, "sentinel.txt"), "tower appdata");

            int exitCode;
            string stderr;
            using (File.Open(Path.Combine(target, "busy.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                (exitCode, stderr) = await RunRemovalHelperAsync(target);
                Assert.True(File.Exists(Path.Combine(target, "sentinel.txt")), "sentinel inside the mounted image was deleted");
            }

            Assert.NotEqual(0, exitCode);
            Assert.True(image.IsMounted(), "busy mount must not be force-unmounted");
            Assert.Contains("1.2.3", stderr);
        }
        finally
        {
            image.Dispose();
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task MacOSNativeRemoval_TargetThatIsItselfAMount_IsUnmountedBeforeRemoval()
    {
        if (!MacDiskImage.IsSupported)
            return;

        var root = Directory.CreateTempSubdirectory("rr-removal-mount-");
        var target = Path.Combine(root.FullName, "versions", "1.2.3");
        var image = MacDiskImage.Create(Path.Combine(root.FullName, "tower.dmg"), "rrtower");
        try
        {
            image.Attach(target);
            File.WriteAllText(Path.Combine(target, "sentinel.txt"), "tower appdata");

            var (exitCode, stderr) = await RunRemovalHelperAsync(target);

            Assert.Equal("tower appdata", image.ReadFromImage("sentinel.txt", Path.Combine(root.FullName, "verify-mount")));
            Assert.True(exitCode == 0, stderr);
            Assert.False(Directory.Exists(target));
        }
        finally
        {
            image.Dispose();
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task MacOSNativeRemoval_TargetThatIsAMountTheTableMissed_IsNotEmptied()
    {
        // `rm -rfx` stays on the device of the path it is given, so if that path is itself a
        // mount the table didn't show (or one that appeared after it was read), -x alone would
        // empty the mounted filesystem. The helper must compare the target with its parent.
        if (!MacDiskImage.IsSupported)
            return;

        var root = Directory.CreateTempSubdirectory("rr-removal-mount-");
        var target = Path.Combine(root.FullName, "versions", "1.2.3");
        var image = MacDiskImage.Create(Path.Combine(root.FullName, "tower.dmg"), "rrtower");
        try
        {
            image.Attach(target);
            File.WriteAllText(Path.Combine(target, "sentinel.txt"), "tower appdata");

            var (exitCode, stderr) = await RunRemovalHelperAsync(target, overrides: "rr_mounts_under() { :; }");

            Assert.True(File.Exists(Path.Combine(target, "sentinel.txt")), "sentinel inside an unlisted mount at the target was deleted");
            Assert.NotEqual(0, exitCode);
            Assert.Contains("1.2.3", stderr);
        }
        finally
        {
            image.Dispose();
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task MacOSNativeRemoval_NestedMountTheTableMissed_IsNotCrossed()
    {
        if (!MacDiskImage.IsSupported)
            return;

        var root = Directory.CreateTempSubdirectory("rr-removal-mount-");
        var workDir = CreateRemovalWorkTree(root.FullName);
        var mountPoint = Path.Combine(workDir, "rr-53d8adc8282263fc", "_temp", "tower");
        var image = MacDiskImage.Create(Path.Combine(root.FullName, "tower.dmg"), "rrtower");
        try
        {
            image.Attach(mountPoint);
            File.WriteAllText(Path.Combine(mountPoint, "sentinel.txt"), "tower appdata");

            var (exitCode, _) = await RunRemovalHelperAsync(workDir, overrides: "rr_mounts_under() { :; }");

            Assert.True(File.Exists(Path.Combine(mountPoint, "sentinel.txt")), "sentinel inside an unlisted nested mount was deleted");
            Assert.NotEqual(0, exitCode);
            Assert.False(File.Exists(Path.Combine(workDir, "rr-53d8adc8282263fc", "repo", "README.md")));
        }
        finally
        {
            image.Dispose();
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task MacOSNativeRemoval_WhenDevicesCannotBeRead_RefusesToRemove()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var root = Directory.CreateTempSubdirectory("rr-removal-stat-");
        try
        {
            var workDir = CreateRemovalWorkTree(root.FullName);

            // Both device lookups failing must not compare equal and wave the delete through.
            var (exitCode, stderr) = await RunRemovalHelperAsync(workDir, "stat() { return 1; }");

            Assert.True(File.Exists(Path.Combine(workDir, "rr-53d8adc8282263fc", "repo", "README.md")), "removed a directory whose device could not be checked");
            Assert.NotEqual(0, exitCode);
            Assert.Contains("could not be read", stderr);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task MacOSScriptsWithMountSafeHelpers_AreValidBash()
    {
        if (!File.Exists("/bin/bash"))
            return;

        var builder = CreateBuilder();
        var removal = builder.BuildRemoval(new HostWorkerRemovalRequest(
            HostWorkerEnrollmentTarget.MacOSNative, "host-1", "Mac Host", "mac-host", null, "/Users/runner/.runnerrunner", "/Users/runner/work")).RemoteSetupScript;
        var update = Assert.Single(builder.BuildManualUpdate(new HostWorkerManualUpdateRequest(
            HostWorkerEnrollmentTarget.MacOSNative, "host-1", "Mac Mini", "mac-mini", null,
            new HostWorkerEnrollmentProxy(null, null, null),
            new HostWorkerManualUpdatePackage(HostWorkerUpdateSourceKind.Release, "main", "abc123def456",
                "runnerrunner-hostworker-osx-arm64.tar.gz", "https://runner.example.test/asset.tar.gz", "abc", null))).CommandBlocks).Command;

        foreach (var script in new[] { removal, update })
        {
            Assert.Contains("rr_safe_rm() {", script);
            var psi = new System.Diagnostics.ProcessStartInfo("/bin/bash")
            {
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("-n");
            using var process = System.Diagnostics.Process.Start(psi)!;
            await process.StandardInput.WriteAsync(script);
            process.StandardInput.Close();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(process.ExitCode == 0, stderr);
        }
    }

    private static string CreateRemovalWorkTree(string root)
    {
        var workDir = Path.Combine(root, "work");
        Directory.CreateDirectory(Path.Combine(workDir, "rr-53d8adc8282263fc", "repo"));
        File.WriteAllText(Path.Combine(workDir, "rr-53d8adc8282263fc", "repo", "README.md"), "checkout");
        return workDir;
    }

    /// <summary>
    /// Runs only the generated removal script's mount-safe helpers against <paramref name="target"/>.
    /// The full script also unloads the real LaunchAgent, so it is never executed here.
    /// </summary>
    private static async Task<(int ExitCode, string Stderr)> RunRemovalHelperAsync(string target, string overrides = "")
    {
        var command = CreateBuilder().BuildRemoval(new HostWorkerRemovalRequest(
            HostWorkerEnrollmentTarget.MacOSNative, "host-1", "Mac Host", "mac-host", null, null, null)).RemoteSetupScript;
        const string begin = "# --- mount-safe removal helpers ---";
        const string end = "# --- end mount-safe removal helpers ---";
        var start = command.IndexOf(begin, StringComparison.Ordinal);
        var stop = command.IndexOf(end, StringComparison.Ordinal);
        Assert.True(start >= 0 && stop > start, "removal script must contain the mount-safe helper block");
        var helpers = command[start..stop];

        var psi = new System.Diagnostics.ProcessStartInfo("/bin/bash")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("set -euo pipefail\n" + helpers + "\n" + overrides + "\nrr_safe_rm \"$1\"\n");
        psi.ArgumentList.Add("rr-removal-test");
        psi.ArgumentList.Add(target);

        using var process = System.Diagnostics.Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        await stdout;
        return (process.ExitCode, await stderr);
    }

    private static void DeleteTempRoot(DirectoryInfo root)
    {
        try
        {
            root.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void BuildRemoval_ForLinuxDocker_RemovesContainerAndVolumes()
    {
        var builder = CreateBuilder();

        var instructions = builder.BuildRemoval(new HostWorkerRemovalRequest(
            HostWorkerEnrollmentTarget.LinuxDocker,
            "host-1",
            "Linux Docker",
            "linux-docker",
            "abcdef123456",
            "/var/lib/runnerrunner",
            null));

        var command = Assert.Single(instructions.CommandBlocks).Command;
        Assert.Equal(HostWorkerEnrollmentRemoteShell.Bash, instructions.RemoteShell);
        Assert.Contains("container='abcdef123456'", command);
        Assert.Contains("docker compose down -v --remove-orphans", command);
        Assert.Contains("runnerrunner-hostworker-data", command);
        Assert.Contains("label=runnerrunner.managed=true", command);
    }

    [Fact]
    public void BuildRemoval_ForWindowsDocker_RemovesContainerAndVolume()
    {
        var builder = CreateBuilder();

        var instructions = builder.BuildRemoval(new HostWorkerRemovalRequest(
            HostWorkerEnrollmentTarget.WindowsDockerWindows,
            "host-1",
            "Windows Docker",
            "windows-docker",
            "abcdef123456",
            null,
            null));

        var command = Assert.Single(instructions.CommandBlocks).Command;
        Assert.Equal(HostWorkerEnrollmentRemoteShell.PowerShell, instructions.RemoteShell);
        Assert.Contains("$container = 'abcdef123456'", command);
        Assert.Contains("docker rm -f $container", command);
        Assert.Contains("runnerrunner-hostworker-windows-data", command);
        Assert.Contains("label=runnerrunner.managed=true", command);
    }

    [Fact]
    public void GetTargetForHost_UsesContainerizedWindowsTarget()
    {
        var host = new Host
        {
            Name = "windows-container",
            Platform = HostPlatform.Windows,
            IsContainerized = true
        };

        var target = HostWorkerEnrollmentGuideBuilder.GetTargetForHost(host);

        Assert.Equal(HostWorkerEnrollmentTarget.WindowsDockerWindows, target);
    }

    private static HostWorkerEnrollmentGuideBuilder CreateBuilder(Dictionary<string, string?>? values = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values ?? new Dictionary<string, string?>())
            .Build();

        return new HostWorkerEnrollmentGuideBuilder(configuration);
    }
}
