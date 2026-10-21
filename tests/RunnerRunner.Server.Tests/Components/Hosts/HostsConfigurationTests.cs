using Bunit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orleans;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services;
using RunnerRunner.Server.Services.HostWorkers;
using Shiny.DocumentDb;
using Host = RunnerRunner.Core.Models.Host;
using HostsPage = RunnerRunner.Server.Components.Pages.Hosts;

namespace RunnerRunner.Server.Tests.Components.Hosts;

public sealed class HostsConfigurationTests
{
    [Fact]
    public async Task SavingPriorityPersistsAndPreservesBackendLimitsAndCurrentOperationalState()
    {
        await using var context = new BunitContext();
        var store = await CreateStore();
        AddServices(context, store);
        var cut = context.Render<HostsPage>();
        OpenConfiguration(cut);
        Assert.Equal("0", cut.Find("#host-scheduling-priority").GetAttribute("value"));
        cut.Find("#host-scheduling-priority").Change("10");

        var current = (await store.Get<Host>("mac"))!;
        current.IsDraining = true;
        current.PendingHostWorkerUpdateSource = "release";
        current.PendingHostWorkerUpdateVersion = "next";
        current.LastHeartbeat = DateTime.UtcNow.AddSeconds(1);
        current.ObservedFreeDiskBytes = 100;
        await store.Update(current);
        cut.FindAll("button").Single(b => b.TextContent.Contains("Save configuration")).Click();

        var saved = (await store.Get<Host>("mac"))!;
        Assert.Equal(10, saved.SchedulingPriority);
        Assert.Equal(0, saved.MaxDockerContainers);
        Assert.Equal(2, saved.MaxTartVMs);
        Assert.Equal(4, saved.MaxNativeProcesses);
        Assert.Equal("macs", saved.GroupId);
        Assert.Equal("ci", saved.Labels["pool"]);
        Assert.Equal("Mac alias", saved.DisplayName);
        Assert.Equal("/runners", saved.RunnerBasePath);
        Assert.Equal("/work", saved.WorkDirectory);
        Assert.True(saved.IsDraining);
        Assert.Equal("release", saved.PendingHostWorkerUpdateSource);
        Assert.Equal("next", saved.PendingHostWorkerUpdateVersion);
        Assert.Equal(current.LastHeartbeat, saved.LastHeartbeat);
        Assert.Equal(100, saved.ObservedFreeDiskBytes);

        OpenConfiguration(cut);
        Assert.Equal("10", cut.Find("#host-scheduling-priority").GetAttribute("value"));
    }

    [Fact]
    public async Task CancellingPriorityEditDoesNotChangeStoredPreference()
    {
        await using var context = new BunitContext();
        var store = await CreateStore();
        var host = (await store.Get<Host>("mac"))!;
        host.SchedulingPriority = 10;
        await store.Update(host);
        AddServices(context, store);
        var cut = context.Render<HostsPage>();
        OpenConfiguration(cut);
        cut.Find("#host-scheduling-priority").Change("-10");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel").Click();

        Assert.Equal(10, (await store.Get<Host>("mac"))!.SchedulingPriority);
        OpenConfiguration(cut);
        Assert.Equal("10", cut.Find("#host-scheduling-priority").GetAttribute("value"));
    }

    private static void OpenConfiguration(IRenderedComponent<HostsPage> cut)
    {
        cut.Find("button[title='More actions']").Click();
        cut.FindAll(".host-action-dropdown-item").Single(b => b.TextContent.Contains("Configure")).Click();
    }

    private static async Task<IDocumentStore> CreateStore()
    {
        var store = TestDocumentStore.Create();
        await store.Insert(new Host
        {
            Id = "mac",
            Name = "mac",
            DisplayName = "Mac alias",
            Platform = HostPlatform.MacOS,
            MaxDockerContainers = 0,
            MaxTartVMs = 2,
            MaxNativeProcesses = 4,
            GroupId = "macs",
            Labels = new() { ["pool"] = "ci" },
            RunnerBasePath = "/runners",
            WorkDirectory = "/work"
        });
        await store.Insert(new RunnerProfile { Id = "seed", Name = "seed" });
        await store.Remove<RunnerProfile>("seed");
        await store.Insert(new ProvisioningRule { Id = "seed", Name = "seed" });
        await store.Remove<ProvisioningRule>("seed");
        await store.Insert(new RunnerInstance { Id = "seed", RunnerName = "seed" });
        await store.Remove<RunnerInstance>("seed");
        await store.Insert(new WebhookEvent { Id = "seed" });
        await store.Remove<WebhookEvent>("seed");
        return store;
    }

    private static void AddServices(BunitContext context, IDocumentStore store)
    {
        var configuration = new ConfigurationBuilder().Build();
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        var environment = Substitute.For<IWebHostEnvironment>();
        environment.ContentRootPath.Returns(Path.GetTempPath());
        var tasks = new LongRunningTaskService(NullLogger<LongRunningTaskService>.Instance);
        var grainFactory = Substitute.For<IGrainFactory>();
        var dispatcher = Substitute.For<IHostCommandDispatcher>();
        context.Services.AddLogging();
        context.Services.AddSingleton<IConfiguration>(configuration);
        context.Services.AddSingleton(store);
        context.Services.AddSingleton(tasks);
        context.Services.AddSingleton(grainFactory);
        context.Services.AddSingleton(dispatcher);
        context.Services.AddSingleton(new CapacityPlanningService(store));
        context.Services.AddSingleton(new HostWorkerEnrollmentGuideBuilder(configuration));
        context.Services.AddSingleton(new HostWorkerSshSetupService(NullLogger<HostWorkerSshSetupService>.Instance));
        context.Services.AddSingleton(new HostWorkerUpdateService(
            http, configuration,
            new GitHubAuthenticationService(http, NullLogger<GitHubAuthenticationService>.Instance),
            store,
            dispatcher, grainFactory,
            new HostWorkerLocalUpdateStore(configuration, environment),
            tasks,
            NullLogger<HostWorkerUpdateService>.Instance));
    }
}
