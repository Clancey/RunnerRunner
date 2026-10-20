using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Tests.Services;

public class HostDocumentUpdatesTests
{
    [Fact]
    public async Task PartialUpdate_RoundTripsHostFieldTypesWithoutReplacingOtherFields()
    {
        var store = TestDocumentStore.Create();
        var host = new Host { Id = "host-1", Name = "old", MaxNativeProcesses = 0 };
        await store.Insert(host);
        host.Name = "name with \"quotes\"";
        host.Platform = HostPlatform.MacOS;
        host.LastHeartbeat = new DateTime(2026, 10, 19, 12, 34, 56, DateTimeKind.Utc);
        host.IsDraining = true;
        host.Capabilities = ["native", "tart"];
        host.Labels = new Dictionary<string, string> { ["rack/1"] = "a\"b", ["old"] = "value" };
        host.GroupId = "macs";
        host.ObservedFreeDiskBytes = 1234567890123;
        host.MaxNativeProcesses = 5;
        await HostDocumentUpdates.UpdateAsync(store, host,
            h => h.Name, h => h.Platform, h => h.LastHeartbeat, h => h.IsDraining,
            h => h.Capabilities, h => h.Labels, h => h.GroupId, h => h.ObservedFreeDiskBytes);

        var saved = (await store.Get<Host>(host.Id))!;
        Assert.Equal(host.Name, saved.Name);
        Assert.Equal(host.Platform, saved.Platform);
        Assert.Equal(host.LastHeartbeat, saved.LastHeartbeat);
        Assert.True(saved.IsDraining);
        Assert.Equal(host.Capabilities, saved.Capabilities);
        Assert.Equal(host.Labels, saved.Labels);
        Assert.Equal(host.GroupId, saved.GroupId);
        Assert.Equal(host.ObservedFreeDiskBytes, saved.ObservedFreeDiskBytes);
        Assert.Equal(0, saved.MaxNativeProcesses);

        host.GroupId = null;
        host.Labels.Remove("old");
        await HostDocumentUpdates.UpdateAsync(store, host, h => h.GroupId, h => h.Labels);
        saved = (await store.Get<Host>(host.Id))!;
        Assert.Null(saved.GroupId);
        Assert.DoesNotContain("old", saved.Labels.Keys);
    }

    [Fact]
    public async Task PartialUpdate_FailureRollsBackEarlierFields()
    {
        var store = TestDocumentStore.Create();
        var host = new Host { Name = "original", MaxNativeProcesses = 5 };
        await store.Insert(host);
        host.MaxNativeProcesses = 0;

        await Assert.ThrowsAsync<KeyNotFoundException>(() => HostDocumentUpdates.UpdateAsync(
            store, host, h => h.MaxNativeProcesses, h => h.Labels["missing"]));

        Assert.Equal(5, (await store.Get<Host>(host.Id))!.MaxNativeProcesses);
    }
}
