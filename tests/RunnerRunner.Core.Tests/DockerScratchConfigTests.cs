using System.Text.Json;
using RunnerRunner.Core.HostWorkers;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Core.Tests;

public class DockerScratchConfigTests
{
    [Fact]
    public void Json_DefaultsAreOptInAndRoundTripThroughWorkerProtocol()
    {
        var disabled = JsonSerializer.Deserialize<DockerImageConfig>("""{"RegistryUrl":"docker.io","ImageName":"myoung34/github-runner"}""")!;
        Assert.Null(disabled.Scratch);
        var enabled = JsonSerializer.Deserialize<DockerImageConfig>("""{"RegistryUrl":"docker.io","ImageName":"myoung34/github-runner","Scratch":{}}""")!;
        Assert.Equal("/actions-runner/_work", enabled.Scratch!.WorkDirectory);
        Assert.Equal(4096, enabled.Scratch.WorkSizeMb);
        Assert.Equal(1024, enabled.Scratch.TempSizeMb);

        var message = HostWorkerProtocol.CreateMessage("host", "test", new DeployRunnerCommand
        {
            RunnerName = "scratch-runner",
            DockerConfig = enabled
        });
        var restored = HostWorkerProtocol.DeserializePayload<DeployRunnerCommand>(message).DockerConfig!;
        Assert.Equal(JsonSerializer.Serialize(enabled), JsonSerializer.Serialize(restored));
        Assert.Contains("\"scratch\"", message.PayloadJson);
    }

    [Fact]
    public void CopiesAndProfileConversions_DoNotAliasMutableScratch()
    {
        var config = new DockerImageConfig
        {
            RegistryUrl = "docker.io",
            ImageName = "myoung34/github-runner",
            CredentialId = "credential-id",
            Scratch = new() { WorkDirectory = "/home/runner/actions-runner/_work", WorkSizeMb = 8192, TempSizeMb = 512 }
        };
        var profile = new RunnerProfile { Name = "runner", DockerConfig = config };
        var target = RunnerDefinition.FromProfile(profile);
        var materialized = target.ToProfile(new ProvisioningRule { Name = "rule" });
        var copy = config.Clone();

        foreach (var other in new[] { target.DockerConfig!, materialized.DockerConfig!, copy })
        {
            Assert.Equal(JsonSerializer.Serialize(config), JsonSerializer.Serialize(other));
            Assert.NotSame(config, other);
            Assert.NotSame(config.Scratch, other.Scratch);
        }
        materialized.DockerConfig!.Scratch!.WorkSizeMb = 1;
        Assert.Equal(8192, config.Scratch.WorkSizeMb);
        Assert.Equal(8192, target.DockerConfig!.Scratch!.WorkSizeMb);
    }

    [Theory]
    [InlineData("/actions-runner/_work")]
    [InlineData("/home/runner/actions-runner/_work")]
    [InlineData("/opt/runner/_work")]
    [InlineData("/runner/_work")]
    public void CanonicalRunnerPathsAndSizeBounds_AreAccepted(string work)
    {
        var config = new DockerScratchConfig { WorkDirectory = work, WorkSizeMb = 1, TempSizeMb = DockerScratchConfig.MaxSizeMb };
        Assert.Null(config.GetValidationError());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/_work")]
    [InlineData("/usr/bin/_work")]
    [InlineData("/run/runner/_work")]
    [InlineData("/etc/runner/_work")]
    [InlineData("/root/.nuget/_work")]
    [InlineData("/runner/./_work")]
    [InlineData("/runner/work")]
    [InlineData("/runner\n/_work")]
    [InlineData("/runner,exec/_work")]
    [InlineData("C:\\runner\\_work")]
    public void InvalidPaths_AreRejectedWithoutNormalization(string? work)
    {
        var config = new DockerScratchConfig { WorkDirectory = work! };
        Assert.NotNull(config.GetValidationError());
        Assert.Equal(work, config.WorkDirectory);
    }

    [Theory]
    [InlineData(HostPlatform.Windows, true, RunnerProvider.GitHubActions, true)]
    [InlineData(HostPlatform.Linux, false, RunnerProvider.GitHubActions, true)]
    [InlineData(HostPlatform.Linux, true, RunnerProvider.AzureDevOps, true)]
    [InlineData(HostPlatform.Linux, true, RunnerProvider.GitHubActions, false)]
    public void IncompatibleEditorSelections_AreRejected(HostPlatform platform, bool ephemeral, RunnerProvider provider, bool jitDispatch)
    {
        Assert.NotNull(new DockerScratchConfig().GetValidationError(platform, ephemeral, provider, jitDispatch));
    }

    [Fact]
    public void LinuxEphemeralGitHubJit_IsAccepted()
    {
        var config = new DockerScratchConfig();
        Assert.Null(config.GetValidationError(HostPlatform.Linux, true, RunnerProvider.GitHubActions, jitDispatch: true));
        Assert.Null(config.GetRunnerValidationError(true, RunnerProvider.GitHubActions));
    }
}
