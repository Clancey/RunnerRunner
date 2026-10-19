using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using RunnerRunner.Agent.Backends;

namespace RunnerRunner.Agent.Tests.Backends;

public class TartSshTransportTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(5000)]
    public async Task PasswordAuthentication_WorksWithoutControllingTtyAndPreservesLongInput(int payloadLength)
    {
        if (OperatingSystem.IsWindows())
            return;

        var directory = Directory.CreateTempSubdirectory("rr-ssh-test-");
        try
        {
            var ssh = Path.Combine(directory.FullName, "ssh");
            // Reproduce OpenSSH's observed loss of its controlling terminal, not a bad password.
            await File.WriteAllTextAsync(ssh, """
                #!/usr/bin/perl
                use strict;
                use warnings;
                use POSIX ();
                my $pid = fork();
                die "fork failed" unless defined $pid;
                if ($pid) { waitpid($pid, 0); exit($? >> 8); }
                POSIX::setsid() >= 0 or die "setsid failed";
                grep($_ eq '-T', @ARGV) or die "Remote terminal not disabled";
                grep($_ eq '-n' || $_ eq 'BatchMode=yes', @ARGV) and die "Input or password prompting disabled";
                grep($_ eq 'PubkeyAuthentication=no', @ARGV) or die "Host keys enabled";
                grep($_ eq 'PreferredAuthentications=password,keyboard-interactive', @ARGV) or die "Wrong auth policy";
                my $password;
                if (($ENV{'SSH_ASKPASS_REQUIRE'} // '') eq 'force') {
                    my $helper = $ENV{'SSH_ASKPASS'} // die "Missing askpass helper";
                    ((stat($helper))[2] & 0777) == 0700 or die "Unsafe helper mode";
                    open(my $source, '<', $helper) or die "Missing helper";
                    my $contents = do { local $/; <$source> };
                    index($contents, q{p'ass}) < 0 or die "Password persisted";
                    grep(index($_, q{p'ass}) >= 0, @ARGV) and die "Password in argv";
                    open(my $pipe, '-|', $helper, 'Password:') or die "Helper failed";
                    $password = do { local $/; <$pipe> };
                    close($pipe) or die "Helper failed";
                    chomp $password;
                } else {
                    open(my $tty, '+<', '/dev/tty') or die "read_passphrase: can't open /dev/tty";
                    $password = <$tty>;
                }
                $password eq q{p'ass $word "quoted"} or die "Password changed";
                exec('/bin/bash', '-s') or die "exec failed";
                """);
            File.SetUnixFileMode(ssh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var output = Path.Combine(directory.FullName, "output");
            var helperPath = Path.Combine(directory.FullName, "helper-path");
            var payload = new string('x', payloadLength);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await TartBackend.ExecuteSshAsync(
                "admin", "unused", "p'ass $word \"quoted\"",
                $"printf '%s' \"$SSH_ASKPASS\" > '{helperPath}'\nprintf '%s' '{payload}' > '{output}'",
                deadline.Token, TimeSpan.FromSeconds(10), ssh);

            Assert.Equal(payload, await File.ReadAllTextAsync(output));
            Assert.False(File.Exists(await File.ReadAllTextAsync(helperPath)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentCalls_UseIndependentHelpersAndChildOnlyEnvironments()
    {
        if (OperatingSystem.IsWindows())
            return;

        var originalPassword = Environment.GetEnvironmentVariable("RR_SSH_PASSWORD");
        var originalHelper = Environment.GetEnvironmentVariable("SSH_ASKPASS");
        var directory = Directory.CreateTempSubdirectory("rr-ssh-concurrent-");
        try
        {
            var ssh = Path.Combine(directory.FullName, "ssh with spaces");
            await File.WriteAllTextAsync(ssh, """
                #!/bin/sh
                password=$("$SSH_ASKPASS") || exit 1
                test "$password" = "$RR_SSH_PASSWORD" || exit 2
                /bin/bash -s
                """);
            File.SetUnixFileMode(ssh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var results = new[] { Path.Combine(directory.FullName, "one"), Path.Combine(directory.FullName, "two") };
            await Task.WhenAll(results.Select((result, index) => TartBackend.ExecuteSshAsync(
                "admin", "unused", $"probe-{index}",
                $"printf '%s' \"$SSH_ASKPASS\" > '{result}'\nsleep 0.1",
                CancellationToken.None, TimeSpan.FromSeconds(5), ssh)));
            var helpers = await Task.WhenAll(results.Select(result => File.ReadAllTextAsync(result)));
            Assert.NotEqual(helpers[0], helpers[1]);
            Assert.All(helpers, helper => Assert.False(File.Exists(helper)));
            Assert.Equal(originalPassword, Environment.GetEnvironmentVariable("RR_SSH_PASSWORD"));
            Assert.Equal(originalHelper, Environment.GetEnvironmentVariable("SSH_ASKPASS"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Process_DrainsBothOutputsBeforeWritingLargeInputAndPreservesRemoteFailure()
    {
        if (OperatingSystem.IsWindows())
            return;

        var directory = Directory.CreateTempSubdirectory("rr-ssh-pipes-");
        try
        {
            var ssh = Path.Combine(directory.FullName, "ssh");
            await File.WriteAllTextAsync(ssh, """
                #!/bin/sh
                /usr/bin/perl -e 'print STDERR "stderr-marker\n", "E" x 131072'
                /bin/bash -s
                printf 'stdout-marker\n'
                exit 23
                """);
            File.SetUnixFileMode(ssh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var output = Path.Combine(directory.FullName, "output");
            var helperPath = Path.Combine(directory.FullName, "helper-path");
            var payload = new string('x', 131072);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                TartBackend.ExecuteSshAsync("admin", "unused", "probe",
                    $"printf '%s' \"$SSH_ASKPASS\" > '{helperPath}'\nprintf '%s' '{payload}' > '{output}'",
                    CancellationToken.None, TimeSpan.FromSeconds(10), ssh));

            Assert.Contains("exit code 23", error.Message);
            Assert.Contains("stdout-marker", error.Message);
            Assert.Contains("stderr-marker", error.Message);
            Assert.Equal(payload, await File.ReadAllTextAsync(output));
            Assert.False(File.Exists(await File.ReadAllTextAsync(helperPath)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Process_BoundsBlockedInputKillsChildAndRemovesHelper(bool callerCancellation)
    {
        if (OperatingSystem.IsWindows())
            return;

        var directory = Directory.CreateTempSubdirectory("rr-ssh-timeout-");
        Process? child = null;
        try
        {
            var ssh = Path.Combine(directory.FullName, "ssh");
            var helperPath = Path.Combine(directory.FullName, "helper-path");
            var childPath = Path.Combine(directory.FullName, "child-pid");
            await File.WriteAllTextAsync(ssh, $"""
                #!/bin/sh
                printf '%s' "$SSH_ASKPASS" > '{helperPath}'
                sleep 60 &
                printf '%s' "$!" > '{childPath}'
                wait
                """);
            File.SetUnixFileMode(ssh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var cancellation = new CancellationTokenSource();
            if (callerCancellation)
                cancellation.CancelAfter(TimeSpan.FromSeconds(2));
            var started = Stopwatch.StartNew();
            var action = () => TartBackend.ExecuteSshAsync(
                "admin", "unused", "probe", new string('x', 1024 * 1024), cancellation.Token,
                TimeSpan.FromSeconds(callerCancellation ? 30 : 2), ssh);
            if (callerCancellation)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(action);
            else
                await Assert.ThrowsAsync<TimeoutException>(action);

            Assert.True(started.Elapsed < TimeSpan.FromSeconds(15));
            Assert.False(File.Exists(await File.ReadAllTextAsync(helperPath)));
            var pid = int.Parse(await File.ReadAllTextAsync(childPath));
            try
            {
                child = Process.GetProcessById(pid);
                Assert.True(child.HasExited, "The owned child is still running");
            }
            catch (ArgumentException)
            {
                // Reaped processes are no longer discoverable.
            }
        }
        finally
        {
            if (child is { HasExited: false })
                child.Kill();
            child?.Dispose();
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ProcessStartFailure_RemovesAskpassHelper()
    {
        if (OperatingSystem.IsWindows())
            return;

        var existing = Directory.GetFiles(Path.GetTempPath(), "rr-ssh-askpass-*").ToHashSet();
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() =>
            TartBackend.ExecuteSshAsync("admin", "unused", "probe", "true",
                CancellationToken.None, TimeSpan.FromSeconds(2),
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing-ssh")));
        Assert.Empty(Directory.GetFiles(Path.GetTempPath(), "rr-ssh-askpass-*").Except(existing));
    }

    [Fact]
    public async Task Process_CanExecuteWithoutAskpass()
    {
        if (OperatingSystem.IsWindows())
            return;

        var psi = new ProcessStartInfo("/bin/bash", "-s")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        await TartBackend.RunSshProcessAsync(psi, "exit 0",
            TimeSpan.FromSeconds(5), CancellationToken.None, NullLogger.Instance);
    }

    [Fact]
    public async Task Process_DeadlineAlsoBoundsOutputHeldAfterParentExit()
    {
        if (OperatingSystem.IsWindows())
            return;

        var directory = Directory.CreateTempSubdirectory("rr-ssh-held-output-");
        var pidPath = Path.Combine(directory.FullName, "pid");
        try
        {
            var psi = new ProcessStartInfo("/bin/bash", "-s")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var started = Stopwatch.StartNew();
            await Assert.ThrowsAsync<TimeoutException>(() => TartBackend.RunSshProcessAsync(
                psi, $"sleep 60 &\necho $! > '{pidPath}'\nexit 0",
                TimeSpan.FromSeconds(2), CancellationToken.None, NullLogger.Instance));
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(15));
        }
        finally
        {
            if (File.Exists(pidPath))
            {
                var pid = int.Parse(await File.ReadAllTextAsync(pidPath));
                try
                {
                    using var child = Process.GetProcessById(pid);
                    if (!child.HasExited)
                        child.Kill();
                }
                catch (ArgumentException)
                {
                }
            }
            directory.Delete(recursive: true);
        }
    }
}
