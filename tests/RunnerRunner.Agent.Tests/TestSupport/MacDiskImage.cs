using System.Diagnostics;

namespace RunnerRunner.Agent.Tests.TestSupport;

/// <summary>
/// A small HFS+ disk image attached with hdiutil, used to put a real, separate filesystem
/// inside a runner directory the way a CI job mounting a share or image would.
/// </summary>
internal sealed class MacDiskImage : IDisposable
{
    private const string Hdiutil = "/usr/bin/hdiutil";

    private readonly string _imagePath;
    private string? _device;

    private MacDiskImage(string imagePath)
    {
        _imagePath = imagePath;
    }

    public static bool IsSupported => OperatingSystem.IsMacOS() && File.Exists(Hdiutil);

    public string? MountPoint { get; private set; }

    public static MacDiskImage Create(string imagePath, string volumeName)
    {
        Run(Hdiutil, "create", "-quiet", "-size", "4m", "-fs", "HFS+", "-layout", "NONE", "-volname", volumeName, imagePath);
        return new MacDiskImage(imagePath);
    }

    public void Attach(string mountPoint)
    {
        Directory.CreateDirectory(mountPoint);
        var output = Run(Hdiutil, "attach", _imagePath, "-mountpoint", mountPoint, "-nobrowse", "-noverify", "-noautoopen");
        _device = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t', 2)[0].Trim())
            .Last(token => token.StartsWith("/dev/disk", StringComparison.Ordinal));
        MountPoint = mountPoint;
    }

    /// <summary>True when the kernel mount table still lists <see cref="MountPoint"/>.</summary>
    public bool IsMounted()
    {
        if (MountPoint is null || !Directory.Exists(MountPoint))
            return false;
        var resolved = Run("/bin/realpath", MountPoint).Trim();
        return Run("/sbin/mount").Split('\n').Any(line => line.Contains($" on {resolved} (", StringComparison.Ordinal));
    }

    /// <summary>
    /// Reads a file from the image's own filesystem. If cleanup unmounted the image, it is
    /// re-attached at <paramref name="verifyMountPoint"/> first so the check reads the image,
    /// not whatever is now at the original path.
    /// </summary>
    public string? ReadFromImage(string relativePath, string verifyMountPoint)
    {
        if (!IsMounted())
        {
            Detach(force: true);
            Attach(verifyMountPoint);
        }

        var path = Path.Combine(MountPoint!, relativePath);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public void Detach(bool force)
    {
        if (_device is null)
            return;
        var args = new List<string> { "detach", _device };
        if (force)
            args.Add("-force");
        try
        {
            Run(Hdiutil, [.. args]);
        }
        catch (InvalidOperationException) when (force)
        {
            // Already detached.
        }
        _device = null;
        MountPoint = null;
    }

    public void Dispose() => Detach(force: true);

    private static string Run(string fileName, params string[] args)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {fileName}");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"{fileName} {string.Join(' ', args)} timed out");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} {string.Join(' ', args)} failed ({process.ExitCode}): {stderr.Result}");
        return stdout.Result;
    }
}
