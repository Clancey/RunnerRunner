using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using Docker.DotNet;
using Microsoft.Extensions.Logging.Abstractions;
using RunnerRunner.Agent.Backends;
using RunnerRunner.Agent.Tests.TestSupport;
using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Agent.Tests.Backends;

public class DockerScratchTests
{
    // Deliberately deserialize the new JSON rather than constructing Scratch:
    // this exercises StartRunnerAsync even against the pre-feature model.
    private const string ScratchRequestJson = """
        {
          "InstanceId": "scratch-reproduction",
          "RunnerName": "scratch-reproduction",
          "Ephemeral": true,
          "JitConfig": "test-jit-config",
          "DockerConfig": {
            "RegistryUrl": "docker.io",
            "ImageName": "myoung34/github-runner",
            "PullPolicy": 2,
            "Scratch": {
              "WorkDirectory": "/actions-runner/_work",
              "WorkSizeMb": 4096,
              "TempSizeMb": 1024
            }
          }
        }
        """;

    [Fact]
    public async Task StartRunnerAsync_ScratchJson_CreatesTwoTmpfsMounts()
    {
        await using var docker = new DockerHttpStub();
        using var client = new DockerClientConfiguration(docker.Endpoint).CreateClient();
        var backend = new DockerBackend(NullLogger<DockerBackend>.Instance, client);
        var request = JsonSerializer.Deserialize<RunnerStartRequest>(ScratchRequestJson)!;

        await backend.StartRunnerAsync(request);

        var create = Assert.Single(docker.CreateBodies);
        var host = create.GetProperty("HostConfig");
        Assert.True(host.TryGetProperty("Tmpfs", out var tmpfs) && tmpfs.ValueKind == JsonValueKind.Object,
            $"Actual POST /containers/create omitted tmpfs. HostConfig: {host}");
        Assert.Equal(2, tmpfs.EnumerateObject().Count());
        Assert.Equal("rw,exec,nosuid,nodev,size=4096m,mode=1777", tmpfs.GetProperty("/actions-runner/_work").GetString());
        Assert.Equal("rw,exec,nosuid,nodev,size=1024m,mode=1777", tmpfs.GetProperty("/tmp").GetString());
        Assert.False(host.TryGetProperty("Binds", out _));
        Assert.False(host.TryGetProperty("Mounts", out _));
        Assert.False(host.TryGetProperty("Privileged", out _));
        Assert.False(host.TryGetProperty("Memory", out _));
        Assert.False(create.TryGetProperty("User", out _)); // keep image's non-root user
        Assert.DoesNotContain("docker.sock", create.ToString());
        Assert.Contains("rr_expected_work='/actions-runner/_work'", create.GetProperty("Entrypoint")[2].GetString());
        Assert.DoesNotContain("/misleading-image-cwd/_work", create.ToString());
    }

    [Theory]
    [InlineData("linux", true)]
    [InlineData("linux", false)]
    [InlineData("windows", true)]
    [InlineData("windows", false)]
    public async Task StartRunnerAsync_DisabledScratch_PreservesLegacyCreate(string os, bool ephemeral)
    {
        var json = JsonNode.Parse(ScratchRequestJson)!;
        json["Ephemeral"] = ephemeral;
        json["DockerConfig"]!.AsObject().Remove("Scratch");
        var omitted = JsonSerializer.Deserialize<RunnerStartRequest>(json)!;
        json["DockerConfig"]!["Scratch"] = null;
        var explicitNull = JsonSerializer.Deserialize<RunnerStartRequest>(json)!;
        await using var docker = new DockerHttpStub { ImageOs = os };
        using var client = new DockerClientConfiguration(docker.Endpoint).CreateClient();
        var backend = new DockerBackend(NullLogger<DockerBackend>.Instance, client);

        await backend.StartRunnerAsync(omitted);
        await backend.StartRunnerAsync(explicitNull);

        Assert.Equal(2, docker.CreateBodies.Count);
        Assert.Equal(docker.CreateBodies[0].GetRawText(), docker.CreateBodies[1].GetRawText());
        Assert.Equal(ephemeral ? """{"RestartPolicy":{"Name":"no"}}""" : """{"RestartPolicy":{"Name":"unless-stopped"}}""",
            docker.CreateBodies[0].GetProperty("HostConfig").GetRawText());
        Assert.DoesNotContain("Docker scratch", docker.CreateBodies[0].GetRawText());
    }

    [Theory]
    [InlineData("/", 4096, 1024)]
    [InlineData("/tmp", 4096, 1024)]
    [InlineData("/tmp/runner/_work", 4096, 1024)]
    [InlineData("actions-runner/_work", 4096, 1024)]
    [InlineData("/runner/../_work", 4096, 1024)]
    [InlineData("/runner//_work", 4096, 1024)]
    [InlineData("/runner/_work/", 4096, 1024)]
    [InlineData("/runner/'$(touch bad)'/_work", 4096, 1024)]
    [InlineData("/proc/runner/_work", 4096, 1024)]
    [InlineData("/opt/hostedtoolcache/runner/_work", 4096, 1024)]
    [InlineData("/actions-runner/_work", 0, 1024)]
    [InlineData("/actions-runner/_work", 4096, -1)]
    [InlineData("/actions-runner/_work", int.MaxValue, 1024)]
    [InlineData("/actions-runner/_work", 4096, int.MaxValue)]
    public async Task StartRunnerAsync_InvalidScratch_RejectsBeforeAnyDockerRequest(string work, int workMb, int tmpMb)
    {
        var request = JsonSerializer.Deserialize<RunnerStartRequest>(ScratchRequestJson)!;
        request.DockerConfig!.Scratch = new() { WorkDirectory = work, WorkSizeMb = workMb, TempSizeMb = tmpMb };
        await using var docker = new DockerHttpStub();
        using var client = new DockerClientConfiguration(docker.Endpoint).CreateClient();
        var backend = new DockerBackend(NullLogger<DockerBackend>.Instance, client);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => backend.StartRunnerAsync(request));

        Assert.Contains("Docker scratch", error.Message);
        Assert.Empty(docker.Requests);
        Assert.Empty(docker.CreateBodies);
    }

    [Fact]
    public async Task StartRunnerAsync_ScratchWithoutJit_CreatesLegacyContainerInsteadOfFailing()
    {
        // A profile shared with a static or scale-set rule has no JIT config, so the image's own
        // entrypoint chooses the work directory. Failing here would re-provision forever.
        var request = JsonSerializer.Deserialize<RunnerStartRequest>(ScratchRequestJson)!;
        request.JitConfig = null;
        await using var docker = new DockerHttpStub();
        using var client = new DockerClientConfiguration(docker.Endpoint).CreateClient();
        var backend = new DockerBackend(NullLogger<DockerBackend>.Instance, client);

        await backend.StartRunnerAsync(request);

        var host = Assert.Single(docker.CreateBodies).GetProperty("HostConfig");
        Assert.False(host.TryGetProperty("Tmpfs", out var tmpfs) && tmpfs.ValueKind != JsonValueKind.Null,
            $"Non-JIT start must not mount scratch. HostConfig: {host}");
    }

    [Theory]
    [InlineData("windows", true, RunnerProvider.GitHubActions)]
    [InlineData("", true, RunnerProvider.GitHubActions)]
    [InlineData("linux", false, RunnerProvider.GitHubActions)]
    [InlineData("linux", true, RunnerProvider.GiteaActions)]
    public async Task StartRunnerAsync_IncompatibleScratch_RejectsBeforeCreate(
        string os, bool ephemeral, RunnerProvider provider)
    {
        var request = JsonSerializer.Deserialize<RunnerStartRequest>(ScratchRequestJson)!;
        request.Ephemeral = ephemeral;
        request.Provider = provider;
        await using var docker = new DockerHttpStub { ImageOs = os };
        using var client = new DockerClientConfiguration(docker.Endpoint).CreateClient();
        var backend = new DockerBackend(NullLogger<DockerBackend>.Instance, client);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => backend.StartRunnerAsync(request));

        Assert.Contains("Docker scratch", error.Message);
        Assert.Empty(docker.CreateBodies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapturedJitWrapper_WarnsOnWorkPathMismatchButStillStartsRunner(bool mismatch)
    {
        if (OperatingSystem.IsWindows())
            return; // The wrapper and feature are Linux-only; also exercised via bash on macOS.

        var root = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, $"rr-scratch-{Guid.NewGuid():N}"));
        try
        {
            var (_, physicalRoot) = await RunShellAsync(root.FullName, "pwd -P", null);
            var runnerRoot = physicalRoot.Trim();
            var work = Path.Combine(runnerRoot, "_work");
            Directory.CreateDirectory(work);
            await WriteExecutableAsync(Path.Combine(runnerRoot, "run.sh"), "#!/bin/sh\necho runner-invoked \"$@\"\n");

            var request = JsonSerializer.Deserialize<RunnerStartRequest>(ScratchRequestJson)!;
            request.DockerConfig!.Scratch!.WorkDirectory = mismatch ? "/different-runner/_work" : work;
            await using var docker = new DockerHttpStub();
            using var client = new DockerClientConfiguration(docker.Endpoint).CreateClient();
            await new DockerBackend(NullLogger<DockerBackend>.Instance, client).StartRunnerAsync(request);
            var script = Assert.Single(docker.CreateBodies).GetProperty("Entrypoint")[2].GetString()!;
            // Restrict discovery to the stub image, even if CI itself runs in a
            // runner container with a real /actions-runner/run.sh on its filesystem.
            var discoveryFacade = "function [ { if [[ \"$1\" == -x && \"$2\" != ./run.sh ]]; then return 1; fi; builtin [ \"$@\"; }; ";

            var (exit, output) = await RunShellAsync(runnerRoot, discoveryFacade + script, null);

            Assert.Equal(0, exit);
            Assert.Contains("runner-invoked --jitconfig test-jit-config", output);
            Assert.Equal(mismatch, output.Contains("WARNING: Docker scratch is mounted at /different-runner/_work", StringComparison.Ordinal));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static async Task WriteExecutableAsync(string path, string body)
    {
        await File.WriteAllTextAsync(path, body);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static async Task<(int ExitCode, string Output)> RunShellAsync(string cwd, string script, string? tools)
    {
        var start = new ProcessStartInfo("/bin/bash")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        start.Environment["RR_JIT_CONFIG"] = "test-jit-config";
        if (tools != null)
            start.Environment["PATH"] = $"{tools}:/usr/bin:/bin";
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await output + await error);
    }
}
