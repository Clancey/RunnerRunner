using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;

namespace RunnerRunner.Agent.Backends;

public sealed class DockerHostOptions
{
    public string? JobNetwork { get; set; }
    public List<DockerBindGrant> BindGrants { get; set; } = [];
    public List<string> ProtectedSources { get; set; } = [];

    public static bool IsCustomJobNetwork(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name is not ("host" or "none" or "bridge" or "default");

    internal List<(DockerBindMount Mount, DockerBindGrant Grant)> Resolve(RunnerStartRequest request)
    {
        var mounts = request.DockerConfig!.BindMounts;
        if (DockerBindMount.GetValidationError(mounts, request.DockerConfig.Scratch) is { } error)
            throw new InvalidOperationException(error);
        if (mounts.Count == 0)
            return [];
        if (string.IsNullOrWhiteSpace(request.ProvisioningRuleId)
            || string.IsNullOrWhiteSpace(request.RunnerDefinitionId)
            || request.ProfileId != request.RunnerDefinitionId)
            throw new InvalidOperationException("Docker bind mounts require explicit rule and runner-definition identity.");
        if (!IsCustomJobNetwork(JobNetwork))
            throw new InvalidOperationException("Docker bind mounts require a host-configured isolated job bridge network.");

        foreach (var grant in BindGrants)
        {
            if (!DockerBindMount.IsCanonicalPath(grant.Source) || DockerBindMount.IsProtectedPath(grant.Source)
                || grant.Source.Split('/').Length < 4
                || ProtectedSources.Any(path => DockerBindMount.Overlaps(grant.Source, path))
                || string.IsNullOrWhiteSpace(grant.ProvisioningRuleId) || string.IsNullOrWhiteSpace(grant.RunnerDefinitionId))
                throw new InvalidOperationException($"Docker bind grant '{grant.Id}' has an unsafe source or missing identity.");
        }
        for (var i = 0; i < BindGrants.Count; i++)
        {
            var grant = BindGrants[i];
            if (BindGrants.Take(i).Any(other => other.Id == grant.Id
                || (DockerBindMount.Overlaps(other.Source, grant.Source)
                    && !(other.Source == grant.Source && other.ReadOnly && grant.ReadOnly))))
                throw new InvalidOperationException("Docker bind grant IDs must be unique; source trees must be disjoint except identical read-only grants.");
        }

        return mounts.Select(mount =>
        {
            var grant = BindGrants.SingleOrDefault(g => g.Id == mount.GrantId
                && g.ProvisioningRuleId == request.ProvisioningRuleId
                && g.RunnerDefinitionId == request.RunnerDefinitionId)
                ?? throw new InvalidOperationException($"Docker bind grant '{mount.GrantId}' is not authorized for this rule and runner definition.");
            if (grant.ReadOnly && !mount.ReadOnly)
                throw new InvalidOperationException($"Docker bind grant '{mount.GrantId}' is read-only.");
            return (mount, grant);
        }).ToList();
    }
}

public sealed class DockerBindGrant
{
    public string Id { get; set; } = "";
    public string ProvisioningRuleId { get; set; } = "";
    public string RunnerDefinitionId { get; set; } = "";
    public string Source { get; set; } = "";
    public bool ReadOnly { get; set; } = true;
}
