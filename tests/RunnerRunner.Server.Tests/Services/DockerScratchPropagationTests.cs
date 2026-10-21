using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Serialization;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Grains.State;
using RunnerRunner.Server.Services;

namespace RunnerRunner.Server.Tests.Services;

public class DockerScratchPropagationTests
{
    private static ProvisioningRule CreateRule() => new()
    {
        Name = "scratch rule",
        Type = ProvisioningType.Webhook,
        RunnerDefinitions =
        [
            new()
            {
                Name = "scratch runner",
                DockerConfig = new()
                {
                    RegistryUrl = "docker.io",
                    ImageName = "myoung34/github-runner",
                    Scratch = new() { WorkDirectory = "/runner/_work", WorkSizeMb = 8192, TempSizeMb = 512 },
                    BindMounts = [new() { GrantId = "project-output", ContainerPath = "/ci/output", ReadOnly = false }]
                }
            }
        ]
    };

    [Fact]
    public void GrainMappingOrleansSerializationAndCopy_PreserveScratch()
    {
        var rule = CreateRule();
        var config = ProvisioningRuleGrainConfigMapper.FromRule(rule);
        using var services = new ServiceCollection()
            .AddSerializer(builder => builder.AddAssembly(typeof(DockerImageConfig).Assembly).AddAssembly(typeof(ProvisioningRuleConfig).Assembly))
            .BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();
        var bytes = serializer.SerializeToArray(config);
        var restored = serializer.Deserialize<ProvisioningRuleConfig>(bytes);
        var copied = services.GetRequiredService<DeepCopier>().Copy(config);
        var original = rule.RunnerDefinitions.Single().DockerConfig!;
        foreach (var roundTripped in new[] { config, restored, copied })
        {
            var docker = roundTripped.RunnerDefinitions.Single().DockerConfig!;
            Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(docker));
        }
        copied.RunnerDefinitions[0].DockerConfig!.Scratch!.WorkSizeMb = 1;
        copied.RunnerDefinitions[0].DockerConfig!.BindMounts[0].ContainerPath = "/changed";
        Assert.Equal("/ci/output", original.BindMounts[0].ContainerPath);
        Assert.Equal("/ci/output", config.RunnerDefinitions[0].DockerConfig!.BindMounts[0].ContainerPath);
        Assert.Equal(8192, original.Scratch!.WorkSizeMb);
        Assert.Equal(8192, config.RunnerDefinitions[0].DockerConfig!.Scratch!.WorkSizeMb);
        config.RunnerDefinitions[0].DockerConfig!.Scratch!.TempSizeMb = 1;
        Assert.Equal(512, original.Scratch.TempSizeMb);
    }

    [Fact]
    public async Task BackupAndMaterialization_PreserveScratchForRulesAndLegacyProfiles()
    {
        var source = TestDocumentStore.Create();
        var destination = TestDocumentStore.Create();
        var rule = CreateRule();
        var profile = Assert.Single(ProvisioningRuleRunnerResolver.MaterializeRuleRunnerProfiles([rule]));
        await source.Insert(rule);
        await source.Insert(profile);

        var exported = await new SettingsBackupService(source, NullLogger<SettingsBackupService>.Instance).ExportBackupAsync(null);
        await using var stream = new MemoryStream(exported.Bytes);
        await new SettingsBackupService(destination, NullLogger<SettingsBackupService>.Instance).ImportBackupAsync(stream, null);

        var restoredRule = await destination.Get<ProvisioningRule>(rule.Id);
        var restoredProfile = await destination.Get<RunnerProfile>(profile.Id);
        Assert.NotNull(restoredRule);
        Assert.NotNull(restoredProfile);
        Assert.Equal(JsonSerializer.Serialize(profile.DockerConfig), JsonSerializer.Serialize(restoredProfile.DockerConfig));
        Assert.Equal(JsonSerializer.Serialize(profile.DockerConfig), JsonSerializer.Serialize(restoredRule.RunnerDefinitions.Single().DockerConfig));
        var (_, resolved) = await ProvisioningRuleRunnerResolver.ResolveProfileAsync(destination, restoredRule);
        Assert.Equal(JsonSerializer.Serialize(profile.DockerConfig), JsonSerializer.Serialize(resolved!.DockerConfig));
    }
}
