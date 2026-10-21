using System.Text.Json;
using Docker.DotNet;
using Microsoft.Extensions.Logging.Abstractions;
using RunnerRunner.Agent.Backends;
using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;
using DockerHttpStub = RunnerRunner.Agent.Tests.Backends.DockerScratchTests.DockerHttpStub;

namespace RunnerRunner.Agent.Tests.Backends;

public class DockerBindTests
{
    private static RunnerStartRequest Request() => JsonSerializer.Deserialize<RunnerStartRequest>("""
        {"InstanceId":"bind-test","RunnerName":"bind-test","ProvisioningRuleId":"rule-a",
         "ProfileId":"target-a","RunnerDefinitionId":"target-a","DockerConfig":{
           "RegistryUrl":"docker.io","ImageName":"runner","PullPolicy":2,
           "BindMounts":[
             {"GrantId":"assets","ContainerPath":"/ci/assets","ReadOnly":true},
             {"GrantId":"output","ContainerPath":"/ci/output","ReadOnly":false}
           ]}}
        """)!;

    private static DockerHostOptions Options() => new()
    {
        JobNetwork = "ci-jobs",
        BindGrants =
        [
            new() { Id = "assets", ProvisioningRuleId = "rule-a", RunnerDefinitionId = "target-a", Source = "/srv/ci/assets" },
            new() { Id = "output", ProvisioningRuleId = "rule-a", RunnerDefinitionId = "target-a", Source = "/srv/ci/project-a", ReadOnly = false }
        ]
    };

    [Fact]
    public async Task AuthorizedJson_EmitsExactReadOnlyAndWritableTypedMountsAndHostNetwork()
    {
        await using var docker = new DockerHttpStub();
        using var client = new DockerClientConfiguration(docker.Endpoint).CreateClient();
        var checks = 0;
        var backend = new DockerBackend(NullLogger<DockerBackend>.Instance, client, Options(), (_, sources) =>
        {
            Assert.Equal(["/srv/ci/assets", "/srv/ci/project-a"], sources);
            checks++;
        });
        await backend.StartRunnerAsync(Request());
        Assert.Equal(3, checks);
        var host = Assert.Single(docker.CreateBodies).GetProperty("HostConfig");
        var mounts = host.GetProperty("Mounts").EnumerateArray().ToArray();
        Assert.Equal(2, mounts.Length);
        Assert.Equal("/srv/ci/assets", mounts[0].GetProperty("Source").GetString());
        Assert.Equal("/ci/assets", mounts[0].GetProperty("Target").GetString());
        Assert.True(mounts[0].GetProperty("ReadOnly").GetBoolean());
        Assert.Equal("/srv/ci/project-a", mounts[1].GetProperty("Source").GetString());
        Assert.False(mounts[1].TryGetProperty("ReadOnly", out var rw) && rw.GetBoolean());
        foreach (var mount in mounts)
        {
            Assert.Equal("bind", mount.GetProperty("Type").GetString());
            Assert.True(mount.GetProperty("BindOptions").GetProperty("NonRecursive").GetBoolean());
            Assert.Equal("rprivate", mount.GetProperty("BindOptions").GetProperty("Propagation").GetString());
            Assert.False(mount.GetProperty("BindOptions").TryGetProperty("CreateMountpoint", out var create) && create.GetBoolean());
        }
        Assert.Equal("ci-network-id", host.GetProperty("NetworkMode").GetString());
        Assert.False(host.TryGetProperty("Binds", out _));
        Assert.False(host.TryGetProperty("Privileged", out _));
        Assert.DoesNotContain("docker.sock", host.ToString());
    }

    [Theory]
    [InlineData("rule")]
    [InlineData("definition")]
    [InlineData("profile")]
    [InlineData("missing-identity")]
    [InlineData("unknown-grant")]
    [InlineData("read-only")]
    [InlineData("no-network")]
    [InlineData("broad-source")]
    [InlineData("overlapping-source")]
    [InlineData("system-destination")]
    [InlineData("overlapping-destination")]
    [InlineData("scratch-overlap")]
    [InlineData("protected-source")]
    public async Task InvalidGrant_RejectsBeforeDocker(string scenario)
    {
        var request = Request();
        var options = Options();
        switch (scenario)
        {
            case "rule": request.ProvisioningRuleId = "rule-b"; break;
            case "definition": request.RunnerDefinitionId = "target-b"; request.ProfileId = "target-b"; break;
            case "profile": request.ProfileId = "other"; break;
            case "missing-identity": request.ProvisioningRuleId = null; break;
            case "unknown-grant": request.DockerConfig!.BindMounts[0].GrantId = "other"; break;
            case "read-only": request.DockerConfig!.BindMounts[0].ReadOnly = false; break;
            case "no-network": options.JobNetwork = null; break;
            case "broad-source": options.BindGrants[1].Source = "/mnt/user/games"; break;
            case "overlapping-source": options.BindGrants[1].Source = "/srv/ci/assets/project"; break;
            case "system-destination": request.DockerConfig!.BindMounts[0].ContainerPath = "/var/run"; break;
            case "overlapping-destination": request.DockerConfig!.BindMounts[1].ContainerPath = "/ci/assets/out"; break;
            case "scratch-overlap": request.DockerConfig!.Scratch = new() { WorkDirectory = "/ci/assets/_work" }; break;
            case "protected-source": options.ProtectedSources.Add("/srv/ci/project-a"); break;
        }
        await using var docker = new DockerHttpStub();
        using var client = new DockerClientConfiguration(docker.Endpoint).CreateClient();
        var backend = new DockerBackend(NullLogger<DockerBackend>.Instance, client, options, (_, _) => { });
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.StartRunnerAsync(request));
        Assert.Empty(docker.Requests);
    }

    [Theory]
    [InlineData("""{"Id":"n","Name":"host","Driver":"host","Scope":"local"}""")]
    [InlineData("""{"Id":"n","Name":"bridge","Driver":"bridge","Scope":"local"}""")]
    [InlineData("""{"Id":"n","Name":"ci-jobs","Driver":"overlay","Scope":"swarm"}""")]
    [InlineData("""{"Name":"ci-jobs","Driver":"bridge","Scope":"local"}""")]
    public async Task UnsafeOrMalformedNetwork_FailsBeforeCreate(string network)
    {
        await using var docker = new DockerHttpStub { NetworkJson = network };
        using var client = new DockerClientConfiguration(docker.Endpoint).CreateClient();
        var backend = new DockerBackend(NullLogger<DockerBackend>.Instance, client, Options(), (_, _) => { });
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.StartRunnerAsync(Request()));
        Assert.Empty(docker.CreateBodies);
    }

    [Theory]
    [InlineData("1.39", "linux")]
    [InlineData("invalid", "linux")]
    [InlineData("1.47", "windows")]
    public async Task UnsupportedDaemonOrImage_FailsClosed(string version, string os)
    {
        await using var docker = new DockerHttpStub { ApiVersion = version, ImageOs = os };
        using var client = new DockerClientConfiguration(docker.Endpoint).CreateClient();
        var backend = new DockerBackend(NullLogger<DockerBackend>.Instance, client, Options(), (_, _) => { });
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.StartRunnerAsync(Request()));
        Assert.Empty(docker.CreateBodies);
    }

    [Fact]
    public async Task RealVerifier_RejectsRemoteEndpointBeforeAnyDockerCall()
    {
        await using var docker = new DockerHttpStub();
        using var client = new DockerClientConfiguration(docker.Endpoint).CreateClient();
        var backend = new DockerBackend(NullLogger<DockerBackend>.Instance, client, Options());
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.StartRunnerAsync(Request()));
        Assert.Empty(docker.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DaemonMissingSource_IsSurfacedWithoutStarting(bool beforeCreate)
    {
        await using var docker = new DockerHttpStub { MissingSource = beforeCreate, FailStart = !beforeCreate };
        using var client = new DockerClientConfiguration(docker.Endpoint).CreateClient();
        var backend = new DockerBackend(NullLogger<DockerBackend>.Instance, client, Options(), (_, _) => { });
        await Assert.ThrowsAsync<DockerApiException>(() => backend.StartRunnerAsync(Request()));
        if (beforeCreate)
            Assert.DoesNotContain(docker.Requests, request => request.Contains("/start"));
        else
            Assert.Contains(docker.Requests, request => request.StartsWith("DELETE "));
    }

    [Fact]
    public async Task SourceChangedAfterCreate_RemovesContainerWithoutStarting()
    {
        await using var docker = new DockerHttpStub();
        using var client = new DockerClientConfiguration(docker.Endpoint).CreateClient();
        var checks = 0;
        var backend = new DockerBackend(NullLogger<DockerBackend>.Instance, client, Options(), (_, _) =>
        {
            if (++checks == 3)
                throw new InvalidOperationException("source replaced");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.StartRunnerAsync(Request()));
        Assert.Single(docker.CreateBodies);
        Assert.DoesNotContain(docker.Requests, request => request.Contains("/start"));
        Assert.Contains(docker.Requests, request => request.StartsWith("DELETE "));
    }

    [Fact]
    public void SourcesAndNamespace_RejectLinksMissingPathsNestedMountsAndDifferentViews()
    {
        var root = Path.Combine(AppContext.BaseDirectory, $"bind-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var physical = new DirectoryInfo(root).FullName;
            // macOS's /var and /tmp aliases are not used; tests live underneath the build output.
            var source = Directory.CreateDirectory(Path.Combine(physical, "project")).FullName;
            const string mountInfo = "20 1 8:1 / / rw - ext4 /dev/sda1 rw\n";
            LinuxDockerBindVerifier.ValidateSources([source], mountInfo);
            Assert.Throws<InvalidOperationException>(() => LinuxDockerBindVerifier.ValidateSources([source + "/missing"], mountInfo));
            var link = Path.Combine(physical, "alias");
            Directory.CreateSymbolicLink(link, source);
            Assert.Throws<InvalidOperationException>(() => LinuxDockerBindVerifier.ValidateSources([link], mountInfo));
            Directory.CreateDirectory(Path.Combine(source, "child"));
            Assert.Throws<InvalidOperationException>(() => LinuxDockerBindVerifier.ValidateSources([link + "/child"], mountInfo));
            Assert.Throws<InvalidOperationException>(() => LinuxDockerBindVerifier.ValidateSources([source],
                mountInfo + $"21 20 8:2 / {source}/child rw - ext4 /dev/sdb1 rw\n"));
            Assert.Throws<InvalidOperationException>(() => LinuxDockerBindVerifier.ValidateSources([source],
                mountInfo + $"21 20 8:1 /other-project {source} rw - ext4 /dev/sda1 rw\n"));
            Assert.Throws<InvalidOperationException>(() => LinuxDockerBindVerifier.ValidateSources([source],
                mountInfo + $"21 20 8:1 /other-project {physical} rw - ext4 /dev/sda1 rw\n"));
            Assert.Throws<InvalidOperationException>(() => LinuxDockerBindVerifier.ValidateSources([source],
                mountInfo + $"21 20 8:1 / {source} rw - ext4 /dev/sda1 rw\n"
                + $"22 20 8:1 /other-project {source} rw - ext4 /dev/sda1 rw\n"));
            Assert.Throws<InvalidOperationException>(() => LinuxDockerBindVerifier.ValidateSources([source], "malformed\n"));
            LinuxDockerBindVerifier.ValidateNamespace("mnt:[1]", "mnt:[1]", "/", "/");
            Assert.Throws<InvalidOperationException>(() => LinuxDockerBindVerifier.ValidateNamespace("mnt:[1]", "mnt:[2]", "/", "/"));
            Assert.Throws<InvalidOperationException>(() => LinuxDockerBindVerifier.ValidateNamespace("mnt:[1]", "mnt:[1]", "/", "/chroot"));
            Assert.Throws<InvalidOperationException>(() => LinuxDockerBindVerifier.ValidateNamespace(null, null, "/", "/"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
