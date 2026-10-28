using System.Text.Json;
using Docker.DotNet;
using Microsoft.Extensions.Logging.Abstractions;
using RunnerRunner.Agent.Backends;
using RunnerRunner.Agent.Tests.TestSupport;
using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Agent.Tests.Backends;

public class DockerBindGrantTests : IDisposable
{
    private const string ProfileId = "2b08f49c-301b-4f8c-a3cb-fd7405f9944d";
    private readonly string _root;

    public DockerBindGrantTests()
    {
        // A canonical, writable host directory at least two levels below /.
        _root = Path.Combine(Path.GetTempPath(), "rr-bind-" + Guid.NewGuid().ToString("N"));
        if (!DockerBindGrant.IsCanonicalPath(_root))
            _root = "/tmp/rr-bind-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(_root, "work"));
        Directory.CreateDirectory(Path.Combine(_root, "gradle"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private DockerHostOptions Options(bool perRunner = true) => new()
    {
        BindGrants =
        [
            new DockerBindGrant
            {
                Id = "work", ProfileId = ProfileId, Source = $"{_root}/work",
                Target = "/actions-runner/_work", PerRunner = perRunner
            },
            new DockerBindGrant
            {
                Id = "gradle", ProfileId = ProfileId, Source = $"{_root}/gradle",
                Target = "/opt/gradle-home"
            },
            new DockerBindGrant
            {
                Id = "other-profile", ProfileId = "someone-else", Source = $"{_root}/gradle",
                Target = "/elsewhere", ReadOnly = true
            }
        ]
    };

    private static RunnerStartRequest Request(string profileId = ProfileId, string runner = "rr-ao-linux-jit-1a2b3c4d", DockerScratchConfig? scratch = null) => new()
    {
        ProfileId = profileId,
        InstanceId = "inst-1",
        RunnerName = runner,
        Ephemeral = true,
        Provider = RunnerProvider.GitHubActions,
        JitConfig = "test-jit-config",
        DockerConfig = new DockerImageConfig { RegistryUrl = "", ImageName = "ascension-ci", Tag = "current", PullPolicy = PullPolicy.Never, Scratch = scratch }
    };

    [Fact]
    public async Task StartRunnerAsync_MatchingGrants_MountsHostDirectoriesAndCreatesPerRunnerDirectory()
    {
        await using var docker = new DockerHttpStub();
        using var client = new DockerClientConfiguration(docker.Endpoint).CreateClient();
        var backend = new DockerBackend(NullLogger<DockerBackend>.Instance, client, Options());
        var request = Request();

        await backend.StartRunnerAsync(request);

        var create = Assert.Single(docker.CreateBodies);
        var host = create.GetProperty("HostConfig");
        Assert.False(host.TryGetProperty("Binds", out _));
        Assert.False(host.TryGetProperty("Tmpfs", out _));
        var mounts = host.GetProperty("Mounts").EnumerateArray().ToList();
        Assert.Equal(2, mounts.Count);

        var work = mounts.Single(m => m.GetProperty("Target").GetString() == "/actions-runner/_work");
        Assert.Equal("bind", work.GetProperty("Type").GetString());
        Assert.Equal($"{_root}/work/{request.RunnerName}", work.GetProperty("Source").GetString());
        Assert.False(work.TryGetProperty("ReadOnly", out var ro) && ro.GetBoolean());
        Assert.True(work.GetProperty("BindOptions").GetProperty("NonRecursive").GetBoolean());
        Assert.Equal("rprivate", work.GetProperty("BindOptions").GetProperty("Propagation").GetString());

        var gradle = mounts.Single(m => m.GetProperty("Target").GetString() == "/opt/gradle-home");
        Assert.Equal($"{_root}/gradle", gradle.GetProperty("Source").GetString());

        Assert.Equal($"{_root}/work/{request.RunnerName}", create.GetProperty("Labels").GetProperty(DockerBackend.BindCleanupLabel).GetString());
        Assert.True(Directory.Exists($"{_root}/work/{request.RunnerName}"));
        if (!OperatingSystem.IsWindows())
            Assert.Equal((UnixFileMode)0x1FF | UnixFileMode.StickyBit, File.GetUnixFileMode($"{_root}/work/{request.RunnerName}"));
        Assert.DoesNotContain("docker.sock", create.ToString());
    }

    [Fact]
    public async Task StartRunnerAsync_OtherProfile_CreatesLegacyContainer()
    {
        await using var docker = new DockerHttpStub();
        using var client = new DockerClientConfiguration(docker.Endpoint).CreateClient();
        var backend = new DockerBackend(NullLogger<DockerBackend>.Instance, client, Options());

        await backend.StartRunnerAsync(Request(profileId: "unrelated"));

        var create = Assert.Single(docker.CreateBodies);
        Assert.False(create.GetProperty("HostConfig").TryGetProperty("Mounts", out _));
        Assert.False(create.GetProperty("Labels").TryGetProperty(DockerBackend.BindCleanupLabel, out _));
        Assert.Empty(Directory.EnumerateFileSystemEntries($"{_root}/work"));
    }

    [Fact]
    public async Task StartRunnerAsync_StalePerRunnerDirectory_IsEmptiedFirst()
    {
        var request = Request();
        var stale = $"{_root}/work/{request.RunnerName}";
        Directory.CreateDirectory(Path.Combine(stale, "AscensionOverride"));
        File.WriteAllText(Path.Combine(stale, "AscensionOverride", "leftover.txt"), "old job");
        await using var docker = new DockerHttpStub();
        using var client = new DockerClientConfiguration(docker.Endpoint).CreateClient();
        var backend = new DockerBackend(NullLogger<DockerBackend>.Instance, client, Options());

        await backend.StartRunnerAsync(request);

        Assert.True(Directory.Exists(stale));
        Assert.Empty(Directory.EnumerateFileSystemEntries(stale));
    }

    [Fact]
    public async Task StartRunnerAsync_UnsafeRunnerName_RejectsBeforeAnyDockerRequest()
    {
        await using var docker = new DockerHttpStub();
        using var client = new DockerClientConfiguration(docker.Endpoint).CreateClient();
        var backend = new DockerBackend(NullLogger<DockerBackend>.Instance, client, Options());

        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.StartRunnerAsync(Request(runner: "../escape")));

        Assert.Empty(docker.Requests);
        Assert.Empty(Directory.EnumerateFileSystemEntries($"{_root}/work"));
    }

    [Fact]
    public async Task StartRunnerAsync_GrantOverlappingScratch_RejectsBeforeAnyDockerRequest()
    {
        await using var docker = new DockerHttpStub();
        using var client = new DockerClientConfiguration(docker.Endpoint).CreateClient();
        var backend = new DockerBackend(NullLogger<DockerBackend>.Instance, client, Options());
        var request = Request(scratch: new DockerScratchConfig());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => backend.StartRunnerAsync(request));

        Assert.Contains("overlaps the tmpfs scratch", error.Message);
        Assert.Empty(docker.Requests);
    }

    [Fact]
    public async Task StartRunnerAsync_MissingGrantSource_RejectsBeforeCreate()
    {
        var options = Options();
        options.BindGrants[0].Source = $"{_root}/missing";
        await using var docker = new DockerHttpStub();
        using var client = new DockerClientConfiguration(docker.Endpoint).CreateClient();
        var backend = new DockerBackend(NullLogger<DockerBackend>.Instance, client, options);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => backend.StartRunnerAsync(Request()));

        Assert.Contains("does not exist on this HostWorker", error.Message);
        Assert.Empty(docker.CreateBodies);
    }

    [Theory]
    [InlineData("", "ProfileId")]
    [InlineData("/etc", "Source")]
    [InlineData("/var/run/docker.sock", "Source")]
    [InlineData("/data", "Source")]
    [InlineData("relative/path", "Source")]
    [InlineData("/mnt/user/../etc", "Source")]
    public void GetValidationError_RejectsUnsafeSourcesAndMissingIdentity(string value, string field)
    {
        var grant = new DockerBindGrant { Id = "g", ProfileId = ProfileId, Source = "/mnt/user/appdata/ci/work", Target = "/actions-runner/_work" };
        if (field == "ProfileId") grant.ProfileId = value; else grant.Source = value;

        Assert.NotNull(grant.GetValidationError());
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/etc/ssl")]
    [InlineData("/tmp/work")]
    [InlineData("/runnerrunner")]
    [InlineData("/usr/local/bin")]
    public void GetValidationError_RejectsProtectedTargets(string target)
    {
        var grant = new DockerBindGrant { Id = "g", ProfileId = ProfileId, Source = "/mnt/user/appdata/ci/work", Target = target };

        Assert.NotNull(grant.GetValidationError());
    }

    [Fact]
    public void GetValidationError_AcceptsTypicalGrant()
    {
        var grant = new DockerBindGrant { Id = "ao-work", ProfileId = ProfileId, Source = "/mnt/cache_nvme/appdata/ascension-ci/work", Target = "/actions-runner/_work", PerRunner = true };

        Assert.Null(grant.GetValidationError());
        Assert.NotNull(new DockerBindGrant { Id = "ro", ProfileId = ProfileId, Source = "/mnt/x/y", Target = "/w", PerRunner = true, ReadOnly = true }.GetValidationError());
    }

    [Fact]
    public void Resolve_OverlappingTargetsForOneProfile_Throws()
    {
        var options = new DockerHostOptions
        {
            BindGrants =
            [
                new DockerBindGrant { Id = "a", ProfileId = ProfileId, Source = "/mnt/x/a", Target = "/ci" },
                new DockerBindGrant { Id = "b", ProfileId = ProfileId, Source = "/mnt/x/b", Target = "/ci/inner" }
            ]
        };

        Assert.Throws<InvalidOperationException>(() => options.Resolve(Request()));
    }

    [Fact]
    public async Task StopRunnerAsync_DeletesPerRunnerDirectoryFromContainerLabel()
    {
        var request = Request();
        var directory = $"{_root}/work/{request.RunnerName}";
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "artifact.bin"), "x");
        await using var docker = new DockerHttpStub();
        docker.ContainerLabels[DockerBackend.BindCleanupLabel] = directory;
        using var client = new DockerClientConfiguration(docker.Endpoint).CreateClient();
        var backend = new DockerBackend(NullLogger<DockerBackend>.Instance, client, Options());

        await backend.StopRunnerAsync("0123456789abcdef");

        Assert.False(Directory.Exists(directory));
        Assert.True(Directory.Exists($"{_root}/work"));
        Assert.Contains(docker.Requests, r => r.Contains("/stop", StringComparison.Ordinal));
        Assert.Contains(docker.Requests, r => r.StartsWith("DELETE /", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CleanupOrphanContainerAsync_DeletesPerRunnerDirectoryFromContainerLabel()
    {
        var directory = $"{_root}/work/rr-ao-linux-jit-dead";
        Directory.CreateDirectory(directory);
        await using var docker = new DockerHttpStub();
        docker.ContainerLabels[DockerBackend.BindCleanupLabel] = directory;
        using var client = new DockerClientConfiguration(docker.Endpoint).CreateClient();
        var backend = new DockerBackend(NullLogger<DockerBackend>.Instance, client, Options());

        await backend.CleanupOrphanContainerAsync("0123456789abcdef");

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task StopRunnerAsync_TamperedLabelOutsideGrant_DeletesNothing()
    {
        var outside = $"{_root}/gradle/precious";
        Directory.CreateDirectory(outside);
        await using var docker = new DockerHttpStub();
        docker.ContainerLabels[DockerBackend.BindCleanupLabel] = $"{outside};{_root}/work;{_root}";
        using var client = new DockerClientConfiguration(docker.Endpoint).CreateClient();
        var backend = new DockerBackend(NullLogger<DockerBackend>.Instance, client, Options());

        await backend.StopRunnerAsync("0123456789abcdef");

        Assert.True(Directory.Exists(outside));
        Assert.True(Directory.Exists($"{_root}/work"));
    }

    [Fact]
    public async Task StopRunnerAsync_WithoutPerRunnerGrants_NeverInspects()
    {
        await using var docker = new DockerHttpStub();
        using var client = new DockerClientConfiguration(docker.Endpoint).CreateClient();
        var backend = new DockerBackend(NullLogger<DockerBackend>.Instance, client, Options(perRunner: false));

        await backend.StopRunnerAsync("0123456789abcdef");

        Assert.DoesNotContain(docker.Requests, r => r.StartsWith("GET /", StringComparison.Ordinal));
    }

    [Fact]
    public void SweepOrphanBindDirectories_DeletesOldDirectoriesWithoutContainers()
    {
        var old = $"{_root}/work/rr-ao-linux-jit-old";
        var live = $"{_root}/work/rr-ao-linux-jit-live";
        var young = $"{_root}/work/rr-ao-linux-jit-young";
        foreach (var directory in new[] { old, live, young })
            Directory.CreateDirectory(directory);
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-1));
        Directory.SetLastWriteTimeUtc(live, DateTime.UtcNow.AddHours(-1));
        var backend = new DockerBackend(NullLogger<DockerBackend>.Instance, Options());

        backend.SweepOrphanBindDirectories(new HashSet<string> { "rr-ao-linux-jit-live" });

        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(live));
        Assert.True(Directory.Exists(young));
        Assert.True(Directory.Exists($"{_root}/gradle"));
    }

    [Fact]
    public void RunnerStartRequest_DeserializesProfileIdFromCommandJson()
    {
        // The server already sends ProfileId on DeployRunnerCommand; the lifecycle manager copies it.
        var request = JsonSerializer.Deserialize<RunnerStartRequest>("""{"ProfileId":"abc","RunnerName":"r"}""")!;

        Assert.Equal("abc", request.ProfileId);
    }
}
