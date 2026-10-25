using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services;
using RunnerRunner.Server.Tests.TestSupport;
using Shiny.DocumentDb;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Tests.Services;

public class CreatedSnapshotReconciliationTests
{
    private static readonly DateTime StartedAt = new(2026, 10, 25, 3, 20, 40, DateTimeKind.Utc);
    private const string InstanceId = "created-snapshot-instance";
    private const string ContainerId = "created-snapshot-container";
    private const string EventId = "created-snapshot-event";
    private const string HostId = "docker-host";

    // Tower 2026-10-25: start logged at 03:20:40.828, reconciliation consumed "created"
    // at 03:20:42.383, cleanup at 03:20:42.484, exit 137 at 03:20:52.921.
    // The list response can be captured between create and start, then delivered after
    // successful startup. Model that ordering explicitly, without sleeps or live Docker.
    // Main already guards this race; the deployed 3d2ee433 service did not.
    [Theory]
    [InlineData("exited")]
    [InlineData("dead")]
    public async Task CreatedSnapshotDeliveredAfterSuccessfulStart_DoesNotEndRunner_ButLaterExitDoes(string terminalState)
    {
        var (service, dispatcher, store, cleanup, services) = await CreateAsync();
        using var serviceLifetime = service;
        using var servicesLifetime = services;
        await store.Insert(new WebhookEvent
        {
            Id = EventId,
            InstanceId = InstanceId,
            Action = "queued",
            Status = "provisioned",
            JobId = "snapshot-job"
        });
        await store.Insert(new RunnerInstance
        {
            Id = InstanceId,
            HostId = HostId,
            RunnerName = "snapshot-runner",
            WebhookEventId = EventId,
            JobId = "snapshot-job",
            ProvisioningMode = "dynamic",
            Status = RunnerInstanceStatus.Starting,
            CreatedAt = StartedAt.AddSeconds(-2),
            DeployedAt = StartedAt.AddSeconds(-1)
        });
        var capturedBeforeStart = Report("created");

        // Store effects of the successful RunnerStarted event happen before reconciliation.
        // The fixture intentionally does not claim to exercise Docker or the event transport.
        var record = (await store.Get<RunnerInstance>(InstanceId))!;
        record.Status = RunnerInstanceStatus.Running;
        record.ContainerId = ContainerId;
        record.StartedAt = StartedAt;
        await store.Update(record);

        var deliveredAt = StartedAt.AddSeconds(2);
        await service.ProcessReportAsync(capturedBeforeStart, store, cleanup, deliveredAt);
        await AssertStillRunningAsync(deliveredAt);
        Assert.Equal("created", capturedBeforeStart.Runners.Single().Status);
        Assert.False(capturedBeforeStart.Runners.Single().IsRunning);

        var nextReportAt = StartedAt.AddSeconds(32);
        await service.ProcessReportAsync(Report("running"), store, cleanup, nextReportAt);
        await AssertStillRunningAsync(nextReportAt);

        // Genuine terminal evidence still ends this same runner and retries its queued work.
        var exitedAt = StartedAt.AddSeconds(62);
        await service.ProcessReportAsync(Report(terminalState), store, cleanup, exitedAt);
        record = (await store.Get<RunnerInstance>(InstanceId))!;
        Assert.Equal(RunnerInstanceStatus.Stopped, record.Status);
        Assert.Equal(exitedAt, record.HostReportedGoneAt);
        Assert.Equal("pending", (await store.Get<WebhookEvent>(EventId))!.Status);
        var command = dispatcher.SingleCommand<CleanupOrphanCommand>(HostCommandKind.CleanupOrphan);
        Assert.Equal(ContainerId, command.ContainerId);
        Assert.Equal(ExecutionBackend.Docker, command.Backend);

        async Task AssertStillRunningAsync(DateTime healthAt)
        {
            var actual = (await store.Get<RunnerInstance>(InstanceId))!;
            Assert.True(actual.Status == RunnerInstanceStatus.Running,
                $"Expected Running after delayed snapshot; actual={actual.Status}; " +
                $"reason={actual.StatusMessage}; cleanup commands={dispatcher.Commands.Count(c => c.Kind == HostCommandKind.CleanupOrphan)}");
            Assert.Equal(StartedAt, actual.StartedAt);
            Assert.Null(actual.StoppedAt);
            Assert.Null(actual.HostReportedGoneAt);
            Assert.Equal(healthAt, actual.LastHealthCheck);
            var linkedEvent = (await store.Get<WebhookEvent>(EventId))!;
            Assert.Equal("provisioned", linkedEvent.Status);
            Assert.Equal(InstanceId, linkedEvent.InstanceId);
            Assert.DoesNotContain(dispatcher.Commands, c => c.Kind == HostCommandKind.CleanupOrphan);
        }
    }

    [Theory]
    [InlineData(ExecutionBackend.Docker, "exited")]
    [InlineData(ExecutionBackend.Docker, "dead")]
    [InlineData(ExecutionBackend.Native, "exited")]
    [InlineData(ExecutionBackend.Tart, "stopped")]
    public async Task TerminalOrphan_IsStillCleanedImmediately(ExecutionBackend backend, string state)
    {
        var (service, dispatcher, store, cleanup, services) = await CreateAsync();
        using var serviceLifetime = service;
        using var servicesLifetime = services;
        var report = Report(state);
        var resource = report.Runners.Single();
        resource.Backend = backend;
        resource.ContainerId = backend == ExecutionBackend.Docker ? ContainerId : "";
        resource.ProcessId = backend == ExecutionBackend.Native ? 1234 : null;
        resource.VmName = backend == ExecutionBackend.Tart ? "snapshot-vm" : null;

        await service.ProcessReportAsync(report, store, cleanup, StartedAt);

        var command = dispatcher.SingleCommand<CleanupOrphanCommand>(HostCommandKind.CleanupOrphan);
        Assert.Equal(backend, command.Backend);
        if (backend == ExecutionBackend.Docker)
            Assert.Equal(ContainerId, command.ContainerId);
        else
            Assert.True(string.IsNullOrEmpty(command.ContainerId));
        Assert.Equal(resource.ProcessId, command.ProcessId);
        Assert.Equal(resource.VmName, command.VmName);
    }

    private static ReconciliationReport Report(string state) => new()
    {
        HostId = HostId,
        ReportsBackendAvailability = true,
        Runners =
        [
            new DiscoveredRunnerInfo
            {
                InstanceId = InstanceId,
                RunnerName = "snapshot-runner",
                ContainerId = ContainerId,
                Backend = ExecutionBackend.Docker,
                Status = state,
                IsRunning = state == "running"
            }
        ]
    };

    private static async Task<(ReconciliationService, RecordingHostCommandDispatcher, IDocumentStore,
        RunnerRegistrationCleanupService, ServiceProvider)> CreateAsync()
    {
        var store = await TestDocumentStore.CreateWithWebhookEventsTableAsync();
        await store.Insert(new Host { Id = HostId, Name = HostId, Platform = HostPlatform.Linux });
        // Ensure the instances table exists even in the orphan-only controls.
        await store.Insert(new RunnerInstance { Id = "table-seed", HostId = "other-host", RunnerName = "table-seed" });
        var dispatcher = new RecordingHostCommandDispatcher();
        var cleanup = new RunnerRegistrationCleanupService([], NullLogger<RunnerRegistrationCleanupService>.Instance);
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton<Microsoft.Extensions.Logging.ILogger<ReconciliationService>>(NullLogger<ReconciliationService>.Instance)
            .AddSingleton<IHostCommandDispatcher>(dispatcher)
            .AddSingleton(store)
            .AddSingleton(cleanup)
            .BuildServiceProvider();
        var service = ActivatorUtilities.CreateInstance<ReconciliationService>(services);
        return (service, dispatcher, store, cleanup, services);
    }
}
