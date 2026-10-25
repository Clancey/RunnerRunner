using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Data;
using RunnerRunner.Server.Grains.Interfaces;
using RunnerRunner.Server.Services;
using RunnerRunner.Server.Services.HostWorkers;
using Shiny.DocumentDb;
using Host = RunnerRunner.Core.Models.Host;
using HostsPage = RunnerRunner.Server.Components.Pages.Hosts;

namespace RunnerRunner.Server.Tests.Components.Hosts;

public class HostConfigurationTests
{
    [Fact]
    public async Task SaveFailure_LeavesConfigurationOpenAndDoesNotReportSuccess()
    {
        using var context = new BunitContext();
        var store = TestDocumentStore.Create();
        var grain = Substitute.For<IHostGrain>();
        grain.SetConfiguration(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(),
                Arg.Any<Dictionary<string, string>>())
            .Returns(Task.FromException(new InvalidOperationException("silo unavailable")));
        await AddServicesAsync(context, store, grain);
        var cut = context.Render<HostsPage>();
        OpenConfiguration(cut);
        cut.Find("#native-limit-enabled").Change(false);

        await SaveButton(cut).ClickAsync(new MouseEventArgs());

        Assert.NotEmpty(cut.FindAll("#configure-host-title"));
        Assert.Contains("silo unavailable", cut.Find(".modal-body .alert-danger").TextContent);
        Assert.Equal(5, (await store.Get<Host>("host-1"))!.MaxNativeProcesses);
    }

    [Fact]
    public async Task Save_UpdatesGrainWithoutReplacingNewerHostTelemetry()
    {
        using var context = new BunitContext();
        var store = TestDocumentStore.Create();
        var grain = Substitute.For<IHostGrain>();
        await AddServicesAsync(context, store, grain);
        var cut = context.Render<HostsPage>();
        OpenConfiguration(cut);
        cut.Find("#native-limit-enabled").Change(false);

        var liveHost = (await store.Get<Host>("host-1"))!;
        liveHost.LastHeartbeat = DateTime.UtcNow;
        liveHost.ObservedRunningTartVMs = 2;
        liveHost.IsDraining = true;
        await store.Update(liveHost);

        await SaveButton(cut).ClickAsync(new MouseEventArgs());

        await grain.Received(1).SetConfiguration(0, 3, 0, null, Arg.Any<Dictionary<string, string>>());
        var saved = (await store.Get<Host>("host-1"))!;
        Assert.Equal(liveHost.LastHeartbeat, saved.LastHeartbeat);
        Assert.Equal(2, saved.ObservedRunningTartVMs);
        Assert.True(saved.IsDraining);
    }

    private static void OpenConfiguration(IRenderedComponent<HostsPage> cut)
    {
        cut.Find("button[title='More actions']").Click();
        cut.FindAll(".host-action-dropdown-item")
            .Single(button => button.TextContent.Trim() == "Configure")
            .Click();
    }

    private static AngleSharp.Dom.IElement SaveButton(IRenderedComponent<HostsPage> cut)
        => cut.FindAll("button").Single(button => button.TextContent.Contains("Save configuration"));

    private static async Task AddServicesAsync(BunitContext context, IDocumentStore store, IHostGrain grain)
    {
        var configuration = new ConfigurationBuilder().Build();
        var environment = Substitute.For<IWebHostEnvironment>();
        environment.ContentRootPath.Returns(Path.GetTempPath());
        var factory = Substitute.For<IGrainFactory>();
        factory.GetGrain<IHostGrain>("host-1", null).Returns(grain);
        var httpClients = Substitute.For<IHttpClientFactory>();
        httpClients.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());

        context.Services.AddLogging();
        context.Services.AddSingleton<IConfiguration>(configuration);
        context.Services.AddSingleton(environment);
        context.Services.AddSingleton(store);
        context.Services.AddSingleton(factory);
        context.Services.AddSingleton(httpClients);
        context.Services.AddSingleton(Substitute.For<IHostCommandDispatcher>());
        context.Services.AddSingleton<CapacityPlanningService>();
        context.Services.AddSingleton<GitHubAuthenticationService>();
        context.Services.AddSingleton<LongRunningTaskService>();
        context.Services.AddSingleton<HostWorkerLocalUpdateStore>();
        context.Services.AddSingleton<HostWorkerUpdateService>();
        context.Services.AddSingleton<HostWorkerEnrollmentGuideBuilder>();
        context.Services.AddSingleton<HostWorkerSshSetupService>();
        await DatabaseInitializer.EnsureTablesCreatedAsync(context.Services);
        await store.Insert(new Host
        {
            Id = "host-1",
            Name = "mac-worker",
            Platform = HostPlatform.MacOS,
            IsApproved = true,
            MaxDockerContainers = 0,
            MaxTartVMs = 3,
            MaxNativeProcesses = 5
        });
    }
}
