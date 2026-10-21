using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Tests.Services;

public class HostSchedulingPriorityTests
{
    [Theory]
    [InlineData(ExecutionBackend.Native)]
    [InlineData(ExecutionBackend.Tart)]
    public void SelectsPreferredReadyMacEvenWhenFallbackHasLowerLoad(ExecutionBackend backend)
    {
        var (preferred, fallback, profile, instances) = CreateTopology(backend);

        var result = Select(preferred, fallback, profile, instances);

        Assert.Equal(preferred.Id, result.SelectedHost?.Id);
        Assert.Equal(10, result.Candidates[0].SchedulingPriority);
        Assert.Equal(1, result.Candidates[0].TotalHostLoad);
        Assert.Equal(0, result.Candidates[1].TotalHostLoad);
    }

    [Theory]
    [InlineData(ExecutionBackend.Native, "full")]
    [InlineData(ExecutionBackend.Tart, "full")]
    [InlineData(ExecutionBackend.Tart, "observed-full")]
    [InlineData(ExecutionBackend.Native, "offline")]
    [InlineData(ExecutionBackend.Native, "draining")]
    [InlineData(ExecutionBackend.Native, "low-disk")]
    [InlineData(ExecutionBackend.Native, "backend-disabled")]
    [InlineData(ExecutionBackend.Native, "platform")]
    [InlineData(ExecutionBackend.Native, "capability")]
    [InlineData(ExecutionBackend.Native, "target-group")]
    [InlineData(ExecutionBackend.Native, "rule-group")]
    [InlineData(ExecutionBackend.Native, "rule-label")]
    [InlineData(ExecutionBackend.Native, "rule-host")]
    [InlineData(ExecutionBackend.Native, "requested-label")]
    public void FallsBackWithoutWeakeningConstraints(ExecutionBackend backend, string blockedBy)
    {
        var (preferred, fallback, profile, instances) = CreateTopology(backend);
        var rule = new ProvisioningRule { Name = "macs" };
        IReadOnlyCollection<string>? requestedLabels = null;
        switch (blockedBy)
        {
            case "full":
                preferred.MaxNativeProcesses = 1;
                preferred.MaxTartVMs = 1;
                break;
            case "observed-full":
                preferred.ObservedRunningTartVMs = 2;
                break;
            case "offline":
                preferred.AgentStatus = AgentStatus.Offline;
                break;
            case "draining":
                preferred.IsDraining = true;
                break;
            case "low-disk":
                preferred.ObservedFreeDiskBytes = 1024L * 1024 * 1024;
                preferred.ObservedDiskUsageAt = DateTime.UtcNow;
                break;
            case "backend-disabled":
                preferred.MaxNativeProcesses = 0;
                break;
            case "platform":
                preferred.Platform = HostPlatform.Linux;
                break;
            case "capability":
                profile.RequiredHostCapabilities = ["xcode"];
                fallback.Capabilities.Add("xcode");
                break;
            case "target-group":
                profile.TargetGroupId = "eligible";
                fallback.GroupId = "eligible";
                break;
            case "rule-group":
                rule.TargetGroupId = "eligible";
                fallback.GroupId = "eligible";
                break;
            case "rule-label":
                rule.RequiredHostLabels["pool"] = "eligible";
                fallback.Labels["pool"] = "eligible";
                break;
            case "rule-host":
                rule.TargetHostId = fallback.Id;
                break;
            case "requested-label":
                requestedLabels = ["xcode"];
                fallback.Capabilities.Add("xcode");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(blockedBy));
        }

        var result = Select(preferred, fallback, profile, instances, rule, requestedLabels, minimumFreeDiskGb: 30);

        Assert.Equal(fallback.Id, result.SelectedHost?.Id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(-10)]
    public void EqualPrioritiesPreserveLeastLoadedPlacementAndLabelTieBreak(int priority)
    {
        var (preferred, fallback, profile, instances) = CreateTopology(ExecutionBackend.Native);
        preferred.SchedulingPriority = fallback.SchedulingPriority = priority;

        Assert.Equal(fallback.Id, Select(preferred, fallback, profile, instances).SelectedHost?.Id);
        Assert.Equal(fallback.Id, Select(preferred, fallback, profile, []).SelectedHost?.Id);
    }

    [Fact]
    public void PriorityIsConfigurableAndIndependentOfMachineNamesOrInputOrder()
    {
        var (preferred, fallback, profile, instances) = CreateTopology(ExecutionBackend.Native);
        preferred.Name = "z-builder";
        fallback.Name = "a-builder";
        Assert.Equal(preferred.Id, Select(fallback, preferred, profile, instances).SelectedHost?.Id);

        preferred.SchedulingPriority = -1;
        Assert.Equal(fallback.Id, Select(preferred, fallback, profile, []).SelectedHost?.Id);
    }

    [Fact]
    public void DoesNotSelectAnyHostWhenBothAreUnavailable()
    {
        var (preferred, fallback, profile, instances) = CreateTopology(ExecutionBackend.Native);
        preferred.IsDraining = fallback.IsDraining = true;

        Assert.Null(Select(preferred, fallback, profile, instances).SelectedHost);
    }

    private static (Host Preferred, Host Fallback, RunnerProfile Profile, RunnerInstance[] Instances)
        CreateTopology(ExecutionBackend backend)
    {
        var preferred = new Host
        {
            Id = "m2",
            Name = "M2ini",
            Platform = HostPlatform.MacOS,
            AgentStatus = AgentStatus.Online,
            Capabilities = ["native", "tart"],
            SchedulingPriority = 10,
            MaxNativeProcesses = 2,
            MaxTartVMs = 2
        };
        var fallback = new Host
        {
            Id = "m1",
            Name = "M1ni",
            Platform = HostPlatform.MacOS,
            AgentStatus = AgentStatus.Online,
            Capabilities = ["native", "tart"],
            MaxNativeProcesses = 2,
            MaxTartVMs = 2
        };
        var profile = new RunnerProfile
        {
            Id = "mac-target",
            Name = "mac-target",
            RequiredHostPlatform = HostPlatform.MacOS,
            ExecutionBackend = backend
        };
        RunnerInstance[] instances =
        [
            new()
            {
                RunnerName = "active-on-m2",
                HostId = preferred.Id,
                ProfileId = profile.Id,
                ManagedByRunnerRunner = true,
                Status = RunnerInstanceStatus.Running
            }
        ];
        return (preferred, fallback, profile, instances);
    }

    private static HostSelectionAnalysis Select(
        Host preferred,
        Host fallback,
        RunnerProfile profile,
        RunnerInstance[] instances,
        ProvisioningRule? rule = null,
        IReadOnlyCollection<string>? requestedLabels = null,
        double minimumFreeDiskGb = 0)
        => CapacityPlanningService.AnalyzeHostSelection(
            profile, rule, [preferred, fallback],
            new Dictionary<string, RunnerProfile> { [profile.Id] = profile },
            instances, requireDispatchReadiness: true,
            requestedRunnerLabels: requestedLabels,
            minimumFreeDiskGb: minimumFreeDiskGb);
}
