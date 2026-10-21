using RunnerRunner.Core.Models;

namespace RunnerRunner.Core.Tests;

public class DockerBindMountTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/proc/config")]
    [InlineData("/ci/../output")]
    [InlineData("/ci//output")]
    [InlineData("/ci/output/")]
    [InlineData("/var/run/docker.sock")]
    [InlineData("/sys")]
    [InlineData("/ci/games")]
    [InlineData("/tmp/ci")]
    [InlineData("/ci/$OUTPUT")]
    public void UnsafeDestination_IsRejected(string path) =>
        Assert.NotNull(DockerBindMount.GetValidationError([new() { GrantId = "output", ContainerPath = path }]));

    [Fact]
    public void ConfigClone_DeepCopiesMountsAndDefaultsToNoBinds()
    {
        var original = new DockerImageConfig { RegistryUrl = "docker.io", ImageName = "runner" };
        Assert.Empty(original.BindMounts);
        original.BindMounts.Add(new() { GrantId = "output", ContainerPath = "/ci/output" });
        var clone = original.Clone();
        clone.BindMounts[0].ReadOnly = false;
        Assert.True(original.BindMounts[0].ReadOnly);
        Assert.Null(DockerBindMount.GetValidationError(clone.BindMounts));
    }
}
