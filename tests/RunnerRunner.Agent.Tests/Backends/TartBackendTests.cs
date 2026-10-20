using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
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

    // Reproduces M4ini 2026-10-20 12:37:36Z: after a DarkWake the guest's address was not yet
    // back in the host's ARP cache, so `tart ip --resolver=arp` failed for
    // rr-FashionPact-macOS-VM-jit-b848a12a while `tart list` still showed it running and
    // GitHub showed job 118626348797 in progress on it. The failure was reported as
    // "stopped", and the server marked instance 8aefd1a6 "crashed: stopped". At 11:51:21 the
    // same report was followed one second later by cleanup of the live VM.
    [Fact]
    public async Task GetHealth_IpLookupFailsWhileVmIsRunning_ReportsRunning()
    {
        const string vm = "rr-FashionPact-macOS-VM-jit-b848a12a";
        var calls = new List<string>();
        var backend = CreateBackend(calls, args => args switch
        {
            _ when args.StartsWith("ip ", StringComparison.Ordinal) => (1, $"no IP address found for {vm}"),
            "list --format json" => (0, $$"""[{"Name":"{{vm}}","State":"running"},{"Name":"ghcr.io/cirruslabs/macos","State":"stopped"}]"""),
            _ => throw new InvalidOperationException($"unexpected tart {args}")
        });

        var health = await backend.GetHealthAsync(vm);

        Assert.True(health.IsRunning);
        Assert.Equal("running", health.Status);
        Assert.Equal([$"ip {vm} --resolver=arp", "list --format json"], calls);
    }

    [Fact]
    public async Task GetHealth_IpAndListBothFail_ReportsUnknownRatherThanStopped()
    {
        var backend = CreateBackend([], args => args.StartsWith("ip ", StringComparison.Ordinal)
            ? (1, "no IP address found")
            : (1, "tart: could not open VM storage"));

        var health = await backend.GetHealthAsync("rr-vm");

        Assert.True(health.IsRunning);
        Assert.Equal("unknown", health.Status);
    }

    [Theory]
    [InlineData("""[{"Name":"rr-vm","State":"stopped"}]""", "stopped")]
    [InlineData("""[{"Name":"rr-other","State":"running"}]""", "not_found")]
    public async Task GetHealth_IpLookupFailsAndVmIsNotRunning_ReportsItGone(string listOutput, string expectedStatus)
    {
        var backend = CreateBackend([], args => args.StartsWith("ip ", StringComparison.Ordinal)
            ? (1, "no IP address found")
            : (0, listOutput));

        var health = await backend.GetHealthAsync("rr-vm");

        Assert.False(health.IsRunning);
        Assert.Equal(expectedStatus, health.Status);
    }

    [Fact]
    public async Task GetHealth_IpLookupSucceeds_ReportsRunningWithoutListing()
    {
        var calls = new List<string>();
        var backend = CreateBackend(calls, _ => (0, "192.168.64.12"));

        var health = await backend.GetHealthAsync("rr-vm");

        Assert.True(health.IsRunning);
        Assert.Equal(["ip rr-vm --resolver=arp"], calls);
    }

    private static TartBackend CreateBackend(List<string> calls, Func<string, (int ExitCode, string Output)> tart)
        => new(NullLogger<TartBackend>.Instance, (command, arguments, _) =>
        {
            Assert.Equal("tart", command);
            calls.Add(arguments);
            return Task.FromResult(tart(arguments));
        });
}
