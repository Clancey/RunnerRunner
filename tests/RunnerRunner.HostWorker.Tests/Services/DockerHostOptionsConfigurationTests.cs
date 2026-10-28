using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RunnerRunner.HostWorker.Services;

namespace RunnerRunner.HostWorker.Tests.Services;

public class DockerHostOptionsConfigurationTests
{
    [Fact]
    public void ReadDockerHostOptions_BindsGrantsFromEnvironmentStyleKeys()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HostWorker:Docker:BindGrants:0:Id"] = "ao-work",
                ["HostWorker:Docker:BindGrants:0:ProfileId"] = "2b08f49c-301b-4f8c-a3cb-fd7405f9944d",
                ["HostWorker:Docker:BindGrants:0:Source"] = "/mnt/cache_nvme/appdata/ascension-ci/work",
                ["HostWorker:Docker:BindGrants:0:Target"] = "/actions-runner/_work",
                ["HostWorker:Docker:BindGrants:0:PerRunner"] = "true",
                ["HostWorker:Docker:BindGrants:1:Id"] = "ao-gradle",
                ["HostWorker:Docker:BindGrants:1:ProfileId"] = "2b08f49c-301b-4f8c-a3cb-fd7405f9944d",
                ["HostWorker:Docker:BindGrants:1:Source"] = "/mnt/cache_nvme/appdata/ascension-ci/gradle-home",
                ["HostWorker:Docker:BindGrants:1:Target"] = "/opt/gradle-home"
            })
            .Build();

        var options = HostCommandProcessor.ReadDockerHostOptions(configuration, NullLogger.Instance);

        Assert.Equal(2, options.BindGrants.Count);
        Assert.True(options.BindGrants[0].PerRunner);
        Assert.False(options.BindGrants[1].PerRunner);
        Assert.All(options.BindGrants, grant => Assert.Null(grant.GetValidationError()));
    }

    [Fact]
    public void ReadDockerHostOptions_WithoutSection_HasNoGrants()
    {
        var options = HostCommandProcessor.ReadDockerHostOptions(new ConfigurationBuilder().Build(), NullLogger.Instance);

        Assert.Empty(options.BindGrants);
    }
}
