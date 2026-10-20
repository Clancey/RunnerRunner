using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RunnerRunner.Agent.Backends;
using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Agent.Tests.Backends;

public enum TartLaunch
{
    GitHubJit,
    GitHubStatic,
    Gitea
}

public class TartRunnerEnvironmentTests
{
    [Theory]
    [InlineData(TartLaunch.GitHubJit, false)]
    [InlineData(TartLaunch.GitHubJit, true)]
    [InlineData(TartLaunch.GitHubStatic, false)]
    [InlineData(TartLaunch.GitHubStatic, true)]
    [InlineData(TartLaunch.Gitea, false)]
    [InlineData(TartLaunch.Gitea, true)]
    public async Task RunnerReceivesEnvSetValuesVerbatimWithoutPersistingThem(TartLaunch launch, bool postExit)
    {
        if (OperatingSystem.IsWindows())
            return;

        using var guest = await TartGuestSandbox.CreateAsync();
        // Shaped like the "Apple Deploy" set: a multi-line .p8 key and a password with shell metacharacters.
        var secrets = new Dictionary<string, string>
        {
            ["ASC_KEY_CONTENT"] =
                "-----BEGIN PRIVATE KEY-----\n" +
                $"MIGTAgEAMBMGByqGSM49AgEGCCqGSM49 {guest.Sentinel}\n" +
                "'single' \"double\" $HOME `id` \\ && echo injected\n" +
                "-----END PRIVATE KEY-----\n",
            ["MATCH_PASSWORD"] = $"p a s s && 'q' \"dq\" $(id) ${{HOME}} {guest.Sentinel}",
            ["ASC_KEY_ID"] = "ABC123XYZ",
        };
        var env = new Dictionary<string, string>(secrets)
        {
            [Core.SecretEnvironment.SecretKeysVariable] = "ASC_KEY_CONTENT,MATCH_PASSWORD"
        };

        await guest.SetupAsync(launch, env, postExit);

        var runner = await guest.WaitForRunnerAsync();
        foreach (var (name, value) in secrets)
            Assert.Equal(value, runner.Env.GetValueOrDefault(name));
        var hook = runner.Env.GetValueOrDefault(RunnerRunner.Agent.Services.JobHookScriptBuilder.HookEnvVarName);
        Assert.Equal(guest.Map("/tmp/rr-job-started.sh"), hook);
        Assert.True(File.Exists(hook));
        if (launch == TartLaunch.GitHubJit)
            Assert.Equal(new[] { "--jitconfig", TartGuestSandbox.JitConfig }, runner.Args);
        if (postExit)
            await guest.WaitForPostExitAsync();

        guest.AssertNotPersisted(guest.Sentinel);
    }
}

public class TartRunnerEnvironmentGuardTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JitRunnerStartsWhenNoEnvironmentIsSet(bool postExit)
    {
        if (OperatingSystem.IsWindows())
            return;

        using var guest = await TartGuestSandbox.CreateAsync();

        await guest.SetupAsync(TartLaunch.GitHubJit, new(), postExit);

        var runner = await guest.WaitForRunnerAsync();
        Assert.Equal(new[] { "--jitconfig", TartGuestSandbox.JitConfig }, runner.Args);
        if (postExit)
            await guest.WaitForPostExitAsync();
    }

    [Theory]
    [InlineData(TartLaunch.GitHubJit, false)]
    [InlineData(TartLaunch.GitHubStatic, false)]
    [InlineData(TartLaunch.GitHubJit, true)]
    [InlineData(TartLaunch.GitHubStatic, true)]
    public async Task UnexportableNameIsSkippedWithoutEchoingItsValue(TartLaunch launch, bool postExit)
    {
        if (OperatingSystem.IsWindows())
            return;

        using var guest = await TartGuestSandbox.CreateAsync();
        var env = new Dictionary<string, string>
        {
            ["BAD-NAME"] = $"leaked {guest.Sentinel}",
            ["ASC_KEY_ID"] = "ABC123XYZ",
        };

        var failure = await Record.ExceptionAsync(() => guest.SetupAsync(launch, env, postExit));

        Assert.DoesNotContain(guest.Sentinel, failure?.ToString() ?? "");
        Assert.Null(failure);
        var runner = await guest.WaitForRunnerAsync();
        Assert.Equal("ABC123XYZ", runner.Env.GetValueOrDefault("ASC_KEY_ID"));
        Assert.Contains(guest.Logs, line => line.Contains("BAD-NAME"));
        Assert.DoesNotContain(guest.Logs, line => line.Contains(guest.Sentinel));
        if (postExit)
            await guest.WaitForPostExitAsync();
        guest.AssertNotPersisted(guest.Sentinel);
    }
}

/// <summary>
/// Stands in for a Tart guest. The real setup code runs through the real SSH
/// transport into a fake ssh that execs a local bash; fixed guest paths are
/// mapped into a sandbox, and the runner binaries are stubs that record the
/// environment and arguments they were started with.
/// </summary>
internal sealed class TartGuestSandbox : IDisposable
{
    public const string JitConfig = "eyJqaXQiOiJwcm9iZSJ9";
    private const string SshUser = "rr-guest";

    private readonly SandboxBackend _backend;

    public string Root { get; }
    public string Sentinel { get; } = $"rr-sentinel-{Guid.NewGuid():N}";
    public List<string> Logs { get; } = [];

    private TartGuestSandbox(string root)
    {
        Root = root;
        _backend = new SandboxBackend(this, Path.Combine(root, "host", "ssh"), new ListLogger(Logs));
    }

    public static async Task<TartGuestSandbox> CreateAsync()
    {
        var root = Directory.CreateTempSubdirectory("rr-tart-guest-").FullName;
        var guest = new TartGuestSandbox(root);
        foreach (var dir in new[] { "host", "bin", "tmp", "seen", "home/actions-runner", "home/act-runner" })
            Directory.CreateDirectory(Path.Combine(root, dir));

        await guest.WriteExecutableAsync("host/ssh", $$"""
            #!/bin/sh
            for arg in "$@"; do
              case "$arg" in *{{guest.Sentinel}}*) echo 'env value in ssh argv' >&2; exit 97;; esac
            done
            if env | grep -q '{{guest.Sentinel}}'; then echo 'env value in ssh environment' >&2; exit 98; fi
            PATH='{{root}}/bin':"$PATH"
            export PATH
            exec /bin/bash -s
            """);
        await guest.WriteExecutableAsync("bin/curl", """
            #!/bin/sh
            while [ $# -gt 0 ]; do
              if [ "$1" = -o ]; then [ -e "$2" ] || : > "$2"; shift; fi
              shift
            done
            """);
        await guest.WriteExecutableAsync("bin/tar", "#!/bin/sh\nexit 0\n");
        // Snapshot each launched script (contents + mode) before it can remove itself.
        await guest.WriteExecutableAsync("bin/nohup", $$"""
            #!/bin/sh
            if [ -f "$1" ]; then
              /usr/bin/perl -e 'printf "%o", (stat $ARGV[0])[2] & 07777' "$1" > '{{root}}/seen/launched-'$$.mode
              cp "$1" '{{root}}/seen/launched-'$$
            fi
            exec /usr/bin/nohup "$@"
            """);

        var record = $$"""
            /usr/bin/perl -e 'print map {"$_\0$ENV{$_}\0"} sort keys %ENV' > '{{root}}/seen/env'
            printf '%s\0' "$@" > '{{root}}/seen/argv'
            : > '{{root}}/seen/done'
            """;
        await guest.WriteExecutableAsync("home/actions-runner/run.sh", $"#!/bin/bash\n{record}\n");
        await guest.WriteExecutableAsync("home/actions-runner/config.sh", "#!/bin/sh\nexit 0\n");
        await guest.WriteExecutableAsync("home/act-runner/act_runner",
            $"#!/bin/bash\n[ \"$1\" = daemon ] || exit 0\nshift\n{record}\n");
        return guest;
    }

    public async Task SetupAsync(TartLaunch launch, Dictionary<string, string> env, bool postExit)
    {
        var request = new RunnerStartRequest
        {
            RunnerName = "rr-env-probe",
            Provider = launch == TartLaunch.Gitea ? RunnerProvider.GiteaActions : RunnerProvider.GitHubActions,
            JitConfig = launch == TartLaunch.GitHubJit ? JitConfig : null,
            EnvironmentVariables = new Dictionary<string, string>(env),
            InitSteps = postExit
                ?
                [
                    new ResolvedInitStep
                    {
                        Id = "post",
                        Name = "post",
                        Phase = InitStepPhase.PostExit,
                        Shell = InitStepShell.Bash,
                        Script = $"printf ok > '{Root}/post-ran'",
                        // InitStepResolver seeds every step's env from the runner env.
                        EnvironmentVariables = new Dictionary<string, string>(env)
                    }
                ]
                : []
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await _backend.SetupRunnerViaSsh("rr-env-probe", "192.0.2.1", SshUser, "probe", request, deadline.Token);
    }

    public async Task<(Dictionary<string, string> Env, string[] Args)> WaitForRunnerAsync()
    {
        await WaitForFileAsync(Path.Combine(Root, "seen", "done"), "runner never started");
        var fields = (await File.ReadAllTextAsync(Path.Combine(Root, "seen", "env"))).Split('\0');
        var env = new Dictionary<string, string>();
        for (var i = 0; i + 1 < fields.Length; i += 2)
            env[fields[i]] = fields[i + 1];
        var args = (await File.ReadAllTextAsync(Path.Combine(Root, "seen", "argv"))).Split('\0')[..^1];
        return (env, args);
    }

    public Task WaitForPostExitAsync() =>
        WaitForFileAsync(Path.Combine(Root, "post-ran"), "PostExit step never ran");

    /// <summary>Maps a fixed guest path into the sandbox.</summary>
    public string Map(string guestPath) => MapPaths(guestPath);

    /// <summary>
    /// Fails if <paramref name="value"/> is still on the guest's disk, or was in a
    /// launched script that group or others could read while it existed.
    /// </summary>
    public void AssertNotPersisted(string value)
    {
        foreach (var copy in Directory.EnumerateFiles(Path.Combine(Root, "seen"), "launched-*"))
        {
            if (copy.EndsWith(".mode", StringComparison.Ordinal) || !File.ReadAllText(copy).Contains(value))
                continue;
            var mode = File.ReadAllText(copy + ".mode");
            Assert.True((Convert.ToInt32(mode, 8) & 0x3F) == 0, $"a launched script holding an env value had mode {mode}");
        }
        foreach (var dir in new[] { "tmp", "home" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, dir), "*", SearchOption.AllDirectories))
                Assert.False(File.ReadAllText(file).Contains(value), $"{file} holds an env value");
        }
    }

    public void Dispose()
    {
        // The launched runner and wrapper may still be finishing their final writes.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(Root, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(250);
            }
        }
    }

    private string MapPaths(string text) =>
        Regex.Replace(text, $"{Regex.Escape(Root)}|/Users/{SshUser}/|/tmp/", match => match.Value switch
        {
            "/tmp/" => $"{Root}/tmp/",
            var value when value == Root => value,
            _ => $"{Root}/home/"
        });

    private string MapScript(string script)
    {
        var decodedPayloads = 0;
        var mapped = Regex.Replace(script,
            $"echo '(?<payload>[A-Za-z0-9+/=]+)' \\| base64 -d|{Regex.Escape(Root)}|/Users/{SshUser}/|/tmp/",
            match =>
            {
                if (!match.Groups["payload"].Success)
                    return MapPaths(match.Value);
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups["payload"].Value));
                decodedPayloads++;
                return $"echo '{Convert.ToBase64String(Encoding.UTF8.GetBytes(MapPaths(decoded)))}' | base64 -d";
            });
        if (script.Contains("base64 -d") && decodedPayloads == 0)
            throw new InvalidOperationException("A guest payload escaped sandbox path mapping");
        return mapped;
    }

    private async Task WaitForFileAsync(string path, string failure)
    {
        for (var i = 0; i < 60 && !File.Exists(path); i++)
            await Task.Delay(250);
        if (!File.Exists(path))
        {
            var log = Path.Combine(Root, "tmp", "runner.log");
            Assert.Fail($"{failure}; runner.log: {(File.Exists(log) ? await File.ReadAllTextAsync(log) : "<missing>")}");
        }
    }

    private async Task WriteExecutableAsync(string relativePath, string contents)
    {
        var path = Path.Combine(Root, relativePath);
        await File.WriteAllTextAsync(path, contents.ReplaceLineEndings("\n"));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private sealed class SandboxBackend(TartGuestSandbox guest, string ssh, ILogger<TartBackend> logger)
        : TartBackend(logger)
    {
        internal override Task SshExec(
            string sshUser, string vmIp, string? sshPassword, string script, CancellationToken ct,
            TimeSpan? timeout = null)
        {
            var mapped = guest.MapScript(script);
            return ExecuteSshAsync(sshUser, vmIp, sshPassword, mapped, ct, TimeSpan.FromSeconds(30), ssh);
        }
    }

    private sealed class ListLogger(List<string> lines) : ILogger<TartBackend>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (lines)
                lines.Add($"{formatter(state, exception)} {exception}");
        }
    }
}
