using System.Diagnostics;
using RunnerRunner.Agent.Backends;

namespace RunnerRunner.Agent.Tests.Backends;

public class TartBackendTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PasswordAuthentication_DoesNotOfferHostSshKeys(bool passwordAuthentication)
    {
        var options = TartBackend.BuildSshOptions(passwordAuthentication);
        Assert.Equal(passwordAuthentication, options.Contains("PubkeyAuthentication=no"));
        Assert.Equal(passwordAuthentication, options.Contains("PreferredAuthentications=password,keyboard-interactive"));
        Assert.Equal(passwordAuthentication, options.Contains("BatchMode=no -o PasswordAuthentication=yes -o KbdInteractiveAuthentication=yes"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DetachedRunner_ReleasesSshPipesAndPreservesPreambleFailure(bool validPreamble)
    {
        if (OperatingSystem.IsWindows())
            return;

        var directory = Directory.CreateTempSubdirectory("rr-tart-launch-");
        var log = Path.Combine(directory.FullName, "runner.log");
        using var shell = new Process
        {
            StartInfo = new ProcessStartInfo("/bin/bash", "-s")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        var started = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var script = TartBackend.BuildDetachedRunnerScript(
                validPreamble ? "cd / && JIT_CONFIG=probe" : "false",
                "/bin/sh -c 'printf \"%s\" \"$1\"; sleep 3' _ \"$JIT_CONFIG\"");
            // Isolate the log, without changing the production script's pipe handling.
            script = script.Replace("/tmp/runner.log", $"'{log.Replace("'", "'\\''")}'");
            started = shell.Start();
            var output = shell.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = shell.StandardError.ReadToEndAsync(deadline.Token);
            await shell.StandardInput.WriteLineAsync(script);
            shell.StandardInput.Close();
            await shell.WaitForExitAsync(deadline.Token);
            try
            {
                await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(validPreamble ? 0 : 1, shell.ExitCode);
            }
            finally
            {
                // The harmless background command must finish before removing its log.
                await Task.Delay(TimeSpan.FromSeconds(4), deadline.Token);
                await Task.WhenAll(output, error);
            }

            if (validPreamble)
                Assert.Equal("probe", await File.ReadAllTextAsync(log, deadline.Token));
            else
                Assert.False(File.Exists(log));
        }
        finally
        {
            if (started && !shell.HasExited)
                shell.Kill(entireProcessTree: true);
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ParseListOutput_ParsesRunningAndStoppedVms()
    {
        const string output = """
        [
          { "Name": "rr-managed", "State": "running" },
          { "Name": "external-build-vm", "State": "running" },
          { "Name": "stopped-vm", "State": "stopped" }
        ]
        """;

        var vms = TartBackend.ParseListOutput(output);

        Assert.Equal(3, vms.Count);
        Assert.Equal(2, vms.Count(vm => vm.IsRunning));
        Assert.Contains(vms, vm => vm.Name == "external-build-vm" && vm.IsRunning);
    }

    [Fact]
    public void ParseListOutput_ReturnsEmptyListForWhitespace()
    {
        Assert.Empty(TartBackend.ParseListOutput("   "));
    }

    [Fact]
    public void ParseListOutput_ReadsCaseInsensitivePropertyNames()
    {
        const string output = """
        [
          { "name": "rr-lower", "state": "running" },
          { "NAME": "rr-upper", "STATE": "stopped" }
        ]
        """;

        var vms = TartBackend.ParseListOutput(output);

        Assert.Equal(2, vms.Count);
        Assert.Contains(vms, vm => vm.Name == "rr-lower" && vm.IsRunning);
        Assert.Contains(vms, vm => vm.Name == "rr-upper" && !vm.IsRunning);
    }
}
