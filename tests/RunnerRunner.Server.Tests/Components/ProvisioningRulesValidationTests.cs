using System.Reflection;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Components.Pages;

namespace RunnerRunner.Server.Tests.Components;

public class ProvisioningRulesValidationTests
{
    [Theory]
    [InlineData("", "vrcooking-macos-c064-20261027-cc8ee50d07d9", "")]
    [InlineData("", "fp-macos-xcode27", "")]
    [InlineData(" ", "fp-macos-xcode27", "")]
    [InlineData("ghcr.io", "cirruslabs/macos-sequoia-base", "latest")]
    public void Tart_AcceptsLocalVmOrOciImage(string registry, string image, string tag)
    {
        var runner = new RunnerDefinition
        {
            Name = "macOS",
            ExecutionBackend = ExecutionBackend.Tart,
            TartConfig = new() { RegistryUrl = registry, ImageName = image, Tag = tag }
        };

        var (valid, error) = Validate(runner);

        Assert.True(valid, error);
        Assert.Equal(registry, runner.TartConfig.RegistryUrl);
        Assert.Equal(image, runner.TartConfig.ImageName);
        Assert.Equal(tag, runner.TartConfig.Tag);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("", " \t ")]
    [InlineData("ghcr.io", "")]
    [InlineData("ghcr.io", " \t ")]
    public void Tart_RejectsMissingImage(string registry, string image)
    {
        var (valid, error) = Validate(new RunnerDefinition
        {
            Name = "macOS",
            ExecutionBackend = ExecutionBackend.Tart,
            TartConfig = new() { RegistryUrl = registry, ImageName = image, Tag = "" }
        });

        Assert.False(valid);
        Assert.Equal("Tart runner 'macOS' needs an image name.", error);
    }

    [Theory]
    [InlineData("ghcr.io", "org/runner", true)]
    [InlineData("", "org/runner", false)]
    [InlineData(" \t ", "org/runner", false)]
    [InlineData("ghcr.io", "", false)]
    [InlineData("ghcr.io", " \t ", false)]
    public void Docker_StillRequiresRegistryAndImage(string registry, string image, bool expectedValid)
    {
        var (valid, error) = Validate(new RunnerDefinition
        {
            Name = "Linux",
            ExecutionBackend = ExecutionBackend.Docker,
            DockerConfig = new() { RegistryUrl = registry, ImageName = image }
        });

        Assert.Equal(expectedValid, valid);
        if (!expectedValid)
            Assert.Equal("Docker runner 'Linux' needs a registry and image name.", error);
    }

    private static (bool Valid, string? Error) Validate(RunnerDefinition runner)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var component = new ProvisioningRules();
        typeof(ProvisioningRules).GetField("editRule", flags)!.SetValue(component,
            new ProvisioningRule { Name = "Test rule", RunnerDefinitions = [runner] });

        var valid = (bool)typeof(ProvisioningRules)
            .GetMethod("ValidateRunnerDefinitions", flags)!.Invoke(component, null)!;
        var error = (string?)typeof(ProvisioningRules).GetField("formError", flags)!.GetValue(component);
        return (valid, error);
    }
}
