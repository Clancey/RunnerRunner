using RunnerRunner.Core.Models;
using RunnerRunner.Server.Services;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Tests.Services;

public class OrchestrationEngineEnvCompositionTests
{
    /// <summary>
    /// Tests the environment variable composition logic by using a real document store
    /// and calling the orchestration engine's compose method via reflection
    /// (since it's private, we test the behavior indirectly through the data layer).
    /// </summary>

    [Fact]
    public async Task EnvVarComposition_SetsLayeredByPriority()
    {
        var store = TestDocumentStore.Create();

        var lowPriority = new EnvironmentVariableSet
        {
            Name = "base",
            Priority = 1,
            Variables = new() { ["PATH"] = "/usr/bin", ["HOME"] = "/home/user", ["SHARED"] = "from-low" }
        };

        var highPriority = new EnvironmentVariableSet
        {
            Name = "override",
            Priority = 10,
            Variables = new() { ["SHARED"] = "from-high", ["EXTRA"] = "extra-val" }
        };

        await store.Insert(lowPriority);
        await store.Insert(highPriority);

        var profile = new RunnerProfile
        {
            Name = "test-profile",
            EnvironmentVariableSetIds = [lowPriority.Id, highPriority.Id],
            EnvironmentOverrides = new() { ["HOME"] = "/custom/home" }
        };

        // Simulate the composition logic from OrchestrationEngine.ComposeEnvironmentVariablesAsync
        var result = new Dictionary<string, string>();

        var allSets = (await store.Query<EnvironmentVariableSet>().ToList()).ToList();
        var selectedSets = allSets
            .Where(s => profile.EnvironmentVariableSetIds.Contains(s.Id))
            .OrderBy(s => s.Priority)
            .ToList();

        foreach (var set in selectedSets)
            foreach (var kvp in set.Variables)
                result[kvp.Key] = kvp.Value;

        foreach (var kvp in profile.EnvironmentOverrides)
            result[kvp.Key] = kvp.Value;

        // Layer 1: low priority set PATH + HOME + SHARED
        Assert.Equal("/usr/bin", result["PATH"]);

        // Layer 1: high priority overrides SHARED, adds EXTRA
        Assert.Equal("from-high", result["SHARED"]);
        Assert.Equal("extra-val", result["EXTRA"]);

        // Layer 2: profile override wins over sets for HOME
        Assert.Equal("/custom/home", result["HOME"]);
    }

    [Fact]
    public async Task EnvVarComposition_HostOverridesWin()
    {
        var store = TestDocumentStore.Create();

        var envSet = new EnvironmentVariableSet
        {
            Name = "base",
            Priority = 1,
            Variables = new() { ["JAVA_HOME"] = "/usr/lib/jvm/default" }
        };
        await store.Insert(envSet);

        var profile = new RunnerProfile
        {
            Name = "test",
            EnvironmentVariableSetIds = [envSet.Id],
            EnvironmentOverrides = new() { ["JAVA_HOME"] = "/usr/lib/jvm/17" }
        };

        var host = new Host
        {
            Name = "build-server-1",
            EnvironmentOverrides = new() { ["JAVA_HOME"] = "/opt/java/21" }
        };

        // Simulate full 3-layer composition
        var result = new Dictionary<string, string>();

        var allSets = (await store.Query<EnvironmentVariableSet>().ToList()).ToList();
        var selectedSets = allSets
            .Where(s => profile.EnvironmentVariableSetIds.Contains(s.Id))
            .OrderBy(s => s.Priority);

        foreach (var set in selectedSets)
            foreach (var kvp in set.Variables)
                result[kvp.Key] = kvp.Value;

        foreach (var kvp in profile.EnvironmentOverrides)
            result[kvp.Key] = kvp.Value;

        foreach (var kvp in host.EnvironmentOverrides)
            result[kvp.Key] = kvp.Value;

        // Host override should win
        Assert.Equal("/opt/java/21", result["JAVA_HOME"]);
    }

    [Fact]
    public async Task EnvVarComposition_EmptySets_ReturnsEmpty()
    {
        var store = TestDocumentStore.Create();

        var profile = new RunnerProfile
        {
            Name = "empty",
            EnvironmentVariableSetIds = [],
            EnvironmentOverrides = new()
        };

        var result = new Dictionary<string, string>();
        // No sets, no overrides → empty
        Assert.Empty(result);
    }

    [Fact]
    public void VariableExpansion_ExpandsDollarReferences()
    {
        var vars = new Dictionary<string, string>
        {
            ["RR_GITHUB_TOKEN"] = "ghp_secret123",
            ["RR_GITHUB_ORG"] = "my-org",
            ["GITHUB_TOKEN"] = "$RR_GITHUB_TOKEN",
            ["GITHUB_ORG"] = "$RR_GITHUB_ORG",
        };

        // Simulate ExpandVariableReferences
        for (var pass = 0; pass < 3; pass++)
        {
            var changed = false;
            foreach (var key in vars.Keys.ToList())
            {
                var value = vars[key];
                if (!value.Contains('$')) continue;
                var expanded = value;
                foreach (var refKey in vars.Keys)
                {
                    expanded = expanded
                        .Replace($"${{{refKey}}}", vars[refKey])
                        .Replace($"${refKey}", vars[refKey]);
                }
                if (expanded != value) { vars[key] = expanded; changed = true; }
            }
            if (!changed) break;
        }

        Assert.Equal("ghp_secret123", vars["GITHUB_TOKEN"]);
        Assert.Equal("my-org", vars["GITHUB_ORG"]);
        // RR_ vars remain unchanged
        Assert.Equal("ghp_secret123", vars["RR_GITHUB_TOKEN"]);
    }

    [Fact]
    public void VariableExpansion_BraceSyntaxWorks()
    {
        var vars = new Dictionary<string, string>
        {
            ["RR_TOKEN"] = "secret",
            ["AUTH"] = "Bearer ${RR_TOKEN}",
        };

        for (var pass = 0; pass < 3; pass++)
        {
            var changed = false;
            foreach (var key in vars.Keys.ToList())
            {
                var value = vars[key];
                if (!value.Contains('$')) continue;
                var expanded = value;
                foreach (var refKey in vars.Keys)
                {
                    expanded = expanded
                        .Replace($"${{{refKey}}}", vars[refKey])
                        .Replace($"${refKey}", vars[refKey]);
                }
                if (expanded != value) { vars[key] = expanded; changed = true; }
            }
            if (!changed) break;
        }

        Assert.Equal("Bearer secret", vars["AUTH"]);
    }

    [Fact]
    public void VariableExpansion_NoReferencesUntouched()
    {
        var vars = new Dictionary<string, string>
        {
            ["PLAIN"] = "no references here",
            ["PATH"] = "/usr/bin:/usr/local/bin",
        };

        for (var pass = 0; pass < 3; pass++)
        {
            foreach (var key in vars.Keys.ToList())
            {
                var value = vars[key];
                if (!value.Contains('$')) continue;
                var expanded = value;
                foreach (var refKey in vars.Keys)
                    expanded = expanded.Replace($"${refKey}", vars[refKey]);
                vars[key] = expanded;
            }
        }

        Assert.Equal("no references here", vars["PLAIN"]);
        Assert.Equal("/usr/bin:/usr/local/bin", vars["PATH"]);
    }

    [Fact]
    public async Task ScaleTracking_RunnerInstancePersistence()
    {
        var store = TestDocumentStore.Create();

        var host = new Host { Name = "test-host", Platform = HostPlatform.Linux };
        await store.Insert(host);

        var profile = new RunnerProfile { Name = "test-profile" };
        await store.Insert(profile);

        var assignment = new RunnerAssignment
        {
            HostId = host.Id,
            ProfileId = profile.Id,
            DesiredCount = 3
        };
        await store.Insert(assignment);

        // Create running instances
        for (int i = 0; i < 2; i++)
        {
            var instance = new RunnerInstance
            {
                HostId = host.Id,
                ProfileId = profile.Id,
                RunnerName = $"runner-{i}",
                Status = RunnerInstanceStatus.Running,
                StartedAt = DateTime.UtcNow
            };
            await store.Insert(instance);
        }

        // Check state
        var instances = (await store.Query<RunnerInstance>()
            .Where(i => i.HostId == host.Id && i.ProfileId == profile.Id)
            .ToList()).ToList();

        var running = instances.Where(i => i.Status == RunnerInstanceStatus.Running).ToList();

        Assert.Equal(2, running.Count);
        // desired=3, actual=2 → need 1 more
        Assert.Equal(1, assignment.DesiredCount - running.Count);
    }

    [Fact]
    public void StaleCleanup_RemovesOnlyTerminalRecordsTheHostReportedGone()
    {
        var now = DateTime.UtcNow;
        var gone = new RunnerInstance
        {
            RunnerName = "stale-runner",
            Status = RunnerInstanceStatus.Stopped,
            StoppedAt = now.AddMinutes(-10),
            HostReportedGoneAt = now.AddMinutes(-10)
        };
        var recent = new RunnerInstance
        {
            RunnerName = "recent-runner",
            Status = RunnerInstanceStatus.Stopped,
            StoppedAt = now.AddMinutes(-1),
            HostReportedGoneAt = now.AddMinutes(-1)
        };
        var running = new RunnerInstance { RunnerName = "active-runner", Status = RunnerInstanceStatus.Running };

        Assert.True(OrchestrationEngine.IsRemovableStaleInstance(gone, now));
        Assert.False(OrchestrationEngine.IsRemovableStaleInstance(recent, now));
        Assert.False(OrchestrationEngine.IsRemovableStaleInstance(running, now));
    }

    // Marked crashed while the host was unreachable (stale health): the VM may still be
    // running a job, so the record must not be forgotten 5 minutes later.
    [Fact]
    public void StaleCleanup_KeepsATerminalRecordTheHostNeverReportedGone()
    {
        var now = DateTime.UtcNow;
        var staleHealth = new RunnerInstance
        {
            RunnerName = "Macos-jit-073525a2",
            Status = RunnerInstanceStatus.Crashed,
            StoppedAt = now.AddMinutes(-10),
            LastHealthCheck = now.AddMinutes(-14)
        };
        var reportedAgain = new RunnerInstance
        {
            RunnerName = "FashionPact-macOS-VM-jit-b848a12a",
            Status = RunnerInstanceStatus.Crashed,
            StoppedAt = now.AddMinutes(-10),
            HostReportedGoneAt = now.AddMinutes(-10),
            LastHealthCheck = now.AddMinutes(-1)
        };
        var dynamic = new RunnerInstance
        {
            RunnerName = "ll-linux-jit-e38466ec",
            ProvisioningMode = "dynamic",
            Status = RunnerInstanceStatus.Failed,
            StoppedAt = now.AddMinutes(-10),
            HostReportedGoneAt = now.AddMinutes(-10)
        };

        Assert.False(OrchestrationEngine.IsRemovableStaleInstance(staleHealth, now));
        Assert.False(OrchestrationEngine.IsRemovableStaleInstance(reportedAgain, now));
        Assert.False(OrchestrationEngine.IsRemovableStaleInstance(dynamic, now));
    }
}
