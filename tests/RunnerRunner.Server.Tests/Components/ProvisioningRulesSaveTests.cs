using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orleans;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Components.Pages;
using RunnerRunner.Server.Grains.Interfaces;
using RunnerRunner.Server.Grains.State;
using RunnerRunner.Server.Services;
using Shiny.DocumentDb;

namespace RunnerRunner.Server.Tests.Components;

public class ProvisioningRulesSaveTests
{
    [Fact]
    public async Task EditAndSave_PreservesRuleHostPin()
    {
        var (context, store, ruleGrain) = CreateContext();
        using var _ = context;
        var rule = await InsertPinnedRule(store);

        var cut = context.Render<ProvisioningRules>();
        ClickEdit(cut);
        ClickSave(cut);

        AssertSavedPin(cut, store, rule, "pinned-host");
        await ruleGrain.Received().SetConfig(Arg.Is<ProvisioningRuleConfig>(c => c.TargetHostId == "pinned-host"));
    }

    // HostGrain rewrites the pin on host replacement and Hosts.razor clears it on host deletion;
    // a save from a form opened before either must not restore the stale pin.
    [Theory]
    [InlineData("replacement-host")]
    [InlineData(null)]
    public async Task EditAndSave_KeepsPinChangedWhileFormWasOpen(string? newPin)
    {
        var (context, store, ruleGrain) = CreateContext();
        using var _ = context;
        var rule = await InsertPinnedRule(store);

        var cut = context.Render<ProvisioningRules>();
        ClickEdit(cut);

        var persisted = await store.Get<ProvisioningRule>(rule.Id);
        persisted!.TargetHostId = newPin;
        await store.Update(persisted);

        ClickSave(cut);

        AssertSavedPin(cut, store, rule, newPin);
        await ruleGrain.Received().SetConfig(Arg.Is<ProvisioningRuleConfig>(c => c.TargetHostId == newPin));
    }

    private static (BunitContext Context, IDocumentStore Store, IProvisioningRuleGrain RuleGrain) CreateContext()
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var store = TestDocumentStore.Create();
        var ruleGrain = Substitute.For<IProvisioningRuleGrain>();
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IProvisioningRuleGrain>(Arg.Any<string>(), Arg.Any<string?>()).Returns(ruleGrain);
        AddServices(context, store, grainFactory);
        return (context, store, ruleGrain);
    }

    private static async Task<ProvisioningRule> InsertPinnedRule(IDocumentStore store)
    {
        var runner = RunnerDefinition.FromProfile(new RunnerProfile
        {
            Name = "linux",
            DockerConfig = new() { RegistryUrl = "docker.io", ImageName = "myoung34/github-runner" }
        });
        var rule = new ProvisioningRule
        {
            Name = "Pinned rule",
            Type = ProvisioningType.Webhook,
            Provider = RunnerProvider.GitHubActions,
            Enabled = true,
            UpdatedAt = DateTime.UtcNow.AddDays(-1),
            MaxConcurrent = 1,
            TargetHostId = "pinned-host",
            RunnerDefinitions = [runner]
        };
        await store.Insert(rule);
        await EnsureTable(store, new RunnerProfile { Name = "placeholder" }, x => x.Id);
        await EnsureTable(store, new RunnerRunner.Core.Models.Host { Name = "placeholder" }, x => x.Id);
        await EnsureTable(store, new ProviderCredential { Name = "placeholder" }, x => x.Id);
        await EnsureTable(store, new RegistryCredential { Name = "placeholder", RegistryUrl = "docker.io" }, x => x.Id);
        await EnsureTable(store, new EnvironmentVariableSet { Name = "placeholder" }, x => x.Id);
        await EnsureTable(store, new RunnerInitStepDefinition { Name = "placeholder" }, x => x.Id);
        await EnsureTable(store, new RunnerInstance { RunnerName = "placeholder" }, x => x.Id);
        await EnsureTable(store, new WebhookEvent(), x => x.Id);
        return rule;
    }

    private static void ClickEdit(IRenderedComponent<ProvisioningRules> cut)
    {
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("button"), b => b.TextContent.Trim() == "Edit"));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Edit").Click();
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("button.btn-primary"), b => b.TextContent.Contains("Save")));
    }

    private static void ClickSave(IRenderedComponent<ProvisioningRules> cut) =>
        cut.FindAll("button.btn-primary").First(b => b.TextContent.Contains("Save")).Click();

    private static void AssertSavedPin(IRenderedComponent<ProvisioningRules> cut, IDocumentStore store, ProvisioningRule original, string? expectedPin)
    {
        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("alert-danger", cut.Markup);
            var saved = Assert.Single(store.Query<ProvisioningRule>().ToList().GetAwaiter().GetResult());
            Assert.True(saved.UpdatedAt > original.UpdatedAt, "rule was not saved");
            Assert.Equal(expectedPin, saved.TargetHostId);
        });
    }

    // The SQLite test store creates tables lazily on first insert; the page queries them all on load.
    private static async Task EnsureTable<T>(IDocumentStore store, T placeholder, Func<T, string> id) where T : class
    {
        await store.Insert(placeholder);
        await store.Remove<T>(id(placeholder));
    }

    private static void AddServices(BunitContext context, IDocumentStore store, IGrainFactory grainFactory)
    {
        context.Services.AddSingleton(store);
        context.Services.AddSingleton(new CapacityPlanningService(store));
        context.Services.AddSingleton(new ProvisioningRuleGrainSyncService(
            grainFactory,
            NullLogger<ProvisioningRuleGrainSyncService>.Instance));
        var registryCatalog = Substitute.For<IRegistryCatalogService>();
        registryCatalog.GetTagsAsync(Arg.Any<RegistryCredential>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RegistryTagResponse());
        context.Services.AddSingleton(registryCatalog);
    }
}
