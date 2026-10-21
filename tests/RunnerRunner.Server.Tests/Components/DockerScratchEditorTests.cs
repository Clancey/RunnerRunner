using Bunit;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Components;
using RunnerRunner.Server.Components.Pages;

namespace RunnerRunner.Server.Tests.Components;

public class DockerScratchEditorTests
{
    [Fact]
    public void BindEditor_AddEditAndRemove_PreservesReadOnlyDefault()
    {
        using var context = new BunitContext();
        List<DockerBindMount> selected = [];
        var cut = context.Render<DockerBindMountEditor>(parameters => parameters
            .Add(p => p.ValueChanged, (List<DockerBindMount> value) => selected = value));
        cut.Find("button").Click();
        Assert.True(Assert.Single(selected).ReadOnly);
        cut.Render(parameters => parameters.Add(p => p.Value, selected));
        cut.Find("input[aria-label='Host grant ID']").Change("project-output");
        cut.Find("input[aria-label='Container storage path']").Change("/ci/output");
        cut.Find("input[type=checkbox]").Change(false);
        Assert.Equal("project-output", selected[0].GrantId);
        Assert.Equal("/ci/output", selected[0].ContainerPath);
        Assert.False(selected[0].ReadOnly);
        cut.Find("button.btn-outline-danger").Click();
        Assert.Empty(selected);
    }

    [Fact]
    public void Editor_OptInCreatesDefaultsAndOptOutReturnsNull()
    {
        using var context = new BunitContext();
        DockerScratchConfig? selected = null;
        var cut = context.Render<DockerScratchEditor>(parameters => parameters
            .Add(p => p.Platform, HostPlatform.Linux)
            .Add(p => p.Ephemeral, true)
            .Add(p => p.ValueChanged, (DockerScratchConfig? value) => selected = value));
        Assert.Empty(cut.FindAll("input[type=number]"));

        cut.Find("input[type=checkbox]").Change(true);
        Assert.NotNull(selected);
        Assert.Equal(4096, selected.WorkSizeMb);
        Assert.Equal(1024, selected.TempSizeMb);
        cut.Render(parameters => parameters.Add(p => p.Value, selected));
        Assert.Equal(2, cut.FindAll("input[type=number]").Count);
        cut.Find("input[id$='-work-size']").Change(2048);
        Assert.Equal(2048, selected.WorkSizeMb);
        cut.Find("input[type=checkbox]").Change(false);
        Assert.Null(selected);
    }

    [Fact]
    public void Editor_ShowsActionableValidationAndBudgetHelp()
    {
        using var context = new BunitContext();
        var cut = context.Render<DockerScratchEditor>(parameters => parameters
            .Add(p => p.Value, new DockerScratchConfig())
            .Add(p => p.Platform, HostPlatform.Windows)
            .Add(p => p.Ephemeral, true));
        Assert.Contains("requires Linux", cut.Find("[role=alert]").TextContent);
        Assert.Contains("not total-container memory limits", cut.Markup);
        cut.Render(parameters => parameters.Add(p => p.Platform, HostPlatform.Linux));
        cut.Find("input[id$='-tmp-size']").Change(0);
        Assert.Contains("between 1 and", cut.Find("[role=alert]").TextContent);
        cut.Find("input[id$='-tmp-size']").Change("");
        Assert.Equal(0, cut.Instance.Value!.TempSizeMb);
        Assert.Contains("between 1 and", cut.Find("[role=alert]").TextContent);
        cut.Find("input[id$='-tmp-size']").Change("2147483648");
        Assert.Equal(0, cut.Instance.Value.TempSizeMb);
        cut.Find("input[id$='-work']").Change("/tmp");
        Assert.Contains("canonical absolute Linux path", cut.Find("[role=alert]").TextContent);
        cut.Find("input[id$='-work']").Change("/actions-runner/_work");
        cut.Find("input[id$='-tmp-size']").Change("1024");
        Assert.Empty(cut.FindAll("[role=alert]"));
        cut.Render(parameters => parameters.Add(p => p.JitDispatch, false));
        Assert.Contains("JIT runners", cut.Find("[role=alert]").TextContent);
    }

    [Fact]
    public void RuleEditCopy_IsolatesScratchBeforeSave()
    {
        var profile = new RunnerProfile
        {
            Name = "scratch",
            DockerConfig = new()
            {
                RegistryUrl = "docker.io", ImageName = "myoung34/github-runner", Scratch = new(),
                BindMounts = [new() { GrantId = "assets", ContainerPath = "/ci/assets" }]
            }
        };
        var target = RunnerDefinition.FromProfile(profile);
        var targetCopy = ProvisioningRules.CloneRunnerDefinition(target);
        targetCopy.DockerConfig!.Scratch!.WorkDirectory = "/runner/_work";
        targetCopy.DockerConfig.BindMounts[0].ReadOnly = false;
        Assert.True(target.DockerConfig!.BindMounts[0].ReadOnly);
        Assert.Equal("/actions-runner/_work", target.DockerConfig!.Scratch!.WorkDirectory);
    }
}
