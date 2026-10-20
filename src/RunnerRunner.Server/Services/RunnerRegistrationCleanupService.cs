using Microsoft.Extensions.Logging;
using RunnerRunner.Core.Hub;
using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Providers;
using Shiny.DocumentDb;

namespace RunnerRunner.Server.Services;

/// <summary>
/// What the provider says a runner registration is doing. This is the only signal that
/// survives lost webhooks: RunnerRunner's own job claims go stale when GitHub stops telling
/// it which runner took which job.
/// </summary>
public enum ProviderRunnerActivity
{
    /// <summary>The provider cannot be asked about individual runners; callers keep their prior behavior.</summary>
    NotSupported,

    /// <summary>The provider could not be asked (no credential or scope, or the API call failed). Never treat as idle.</summary>
    Unknown,

    /// <summary>The provider has no registration with this name: the runner finished its job or never registered.</summary>
    NotRegistered,

    /// <summary>Registered and not running a job (online or offline).</summary>
    Idle,

    /// <summary>Registered and running a job.</summary>
    Busy
}

/// <summary>
/// Best-effort provider-side cleanup for runner registrations that may linger
/// after host/process crashes or abrupt teardown.
/// </summary>
public class RunnerRegistrationCleanupService
{
    private readonly IEnumerable<IRunnerProviderPlugin> _providers;
    private readonly ILogger<RunnerRegistrationCleanupService> _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _ownerIsPersonalAccount =
        new(StringComparer.OrdinalIgnoreCase);

    public RunnerRegistrationCleanupService(
        IEnumerable<IRunnerProviderPlugin> providers,
        ILogger<RunnerRegistrationCleanupService> logger)
    {
        _providers = providers;
        _logger = logger;
    }

    public async Task TryRemoveRunnerAsync(
        IDocumentStore store,
        RunnerInstance instance,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instance.RunnerName) || string.IsNullOrWhiteSpace(instance.ProfileId))
            return;

        var profile = await store.Get<RunnerProfile>(instance.ProfileId);
        if (profile == null || string.IsNullOrWhiteSpace(profile.ProviderCredentialId))
            return;

        var credential = await store.Get<ProviderCredential>(profile.ProviderCredentialId);
        if (credential == null)
            return;

        var provider = _providers.FirstOrDefault(p => p.Provider == profile.Provider);
        if (provider == null)
            return;

        try
        {
            var scopedCredential = credential;

            if (profile.Provider == RunnerProvider.GitHubActions)
            {
                var scope = await ResolveGitHubScopeAsync(store, instance);
                scopedCredential = ScopeGitHubCredentialToRepository(
                    credential,
                    scope.Repository,
                    scope.InstallationId);

                if (string.IsNullOrWhiteSpace(scopedCredential.GitHubOrg)
                    && string.IsNullOrWhiteSpace(scopedCredential.GitHubRepo))
                {
                    _logger.LogDebug(
                        "Skipping provider-side cleanup for runner {RunnerName}; no GitHub org/repo scope could be resolved",
                        instance.RunnerName);
                    return;
                }
            }

            await provider.RemoveRunnerAsync(scopedCredential, instance.RunnerName, ct);
            _logger.LogInformation(
                "Attempted provider-side cleanup for runner {RunnerName} via {Provider}",
                instance.RunnerName,
                profile.Provider);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Best-effort provider-side cleanup failed for runner {RunnerName}",
                instance.RunnerName);
        }
    }

    /// <summary>
    /// Asks the provider whether <paramref name="instance"/>'s runner is running a job. The
    /// scopes are checked in the order <see cref="JitConfigService"/> tries to register a
    /// runner in: the event's repository, its owner's org, then the credential's default
    /// repository and owner. A hit in any scope is authoritative; NotRegistered needs every
    /// scope to answer that it has no such runner.
    /// </summary>
    public virtual async Task<ProviderRunnerActivity> GetProviderRunnerActivityAsync(
        IDocumentStore store,
        RunnerInstance instance,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(instance.RunnerName) || string.IsNullOrWhiteSpace(instance.ProfileId))
            return ProviderRunnerActivity.Unknown;

        var profile = await store.Get<RunnerProfile>(instance.ProfileId);
        if (profile == null)
            return ProviderRunnerActivity.Unknown;

        if (profile.Provider != RunnerProvider.GitHubActions)
            return ProviderRunnerActivity.NotSupported;

        var github = _providers.OfType<GitHubActionsProvider>().FirstOrDefault();
        if (github == null)
            return ProviderRunnerActivity.NotSupported;

        if (string.IsNullOrWhiteSpace(profile.ProviderCredentialId))
            return ProviderRunnerActivity.Unknown;

        var credential = await store.Get<ProviderCredential>(profile.ProviderCredentialId);
        if (credential == null)
            return ProviderRunnerActivity.Unknown;

        var scope = await ResolveGitHubScopeAsync(store, instance);
        var scopes = BuildRunnerLookupScopes(credential, scope);
        if (scopes.Count == 0)
            return ProviderRunnerActivity.Unknown;

        var unknown = false;
        var ownersWithNoOrg = new List<(string Owner, ProviderCredential Credential)>();
        foreach (var lookup in scopes)
        {
            try
            {
                var registration = await github.FindRunnerRegistrationAsync(
                    lookup.Credential, lookup.Repository, lookup.Owner, instance.RunnerName, ct, lookup.InstallationId);
                if (registration != null)
                    return registration.Busy ? ProviderRunnerActivity.Busy : ProviderRunnerActivity.Idle;
            }
            catch (HttpRequestException ex) when (lookup.Repository == null && ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // A personal account has no org runner endpoint. That is only "absent" once
                // the owner is confirmed to be a user; an org can 404 here too.
                ownersWithNoOrg.Add((lookup.Owner!, lookup.Credential));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Could not look up runner {RunnerName} in {Scope}; its activity may be unknown",
                    instance.RunnerName, lookup.Repository ?? $"org {lookup.Owner}");
                unknown = true;
            }
        }

        if (unknown)
            return ProviderRunnerActivity.Unknown;

        foreach (var (owner, ownerCredential) in ownersWithNoOrg)
        {
            if (!await IsPersonalAccountAsync(github, ownerCredential, owner, ct))
            {
                _logger.LogWarning(
                    "GitHub returned 404 for org {Owner}'s runners and the account is not confirmed to be personal; treating runner {RunnerName}'s activity as unknown",
                    owner, instance.RunnerName);
                return ProviderRunnerActivity.Unknown;
            }
        }

        return ProviderRunnerActivity.NotRegistered;
    }

    /// <summary>
    /// Asks the provider about a runner the host reports but RunnerRunner has no record of.
    /// The profile comes from the webhook event that named the runner, else from the runner
    /// name's profile prefix. Returns null when no profile (or, for GitHub, no credential)
    /// can be resolved: the caller must then keep the resource.
    /// </summary>
    public virtual async Task<ProviderRunnerActivity?> GetOrphanRunnerActivityAsync(
        IDocumentStore store,
        DiscoveredRunnerInfo runner,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runner.RunnerName))
            return null;

        var linkedEvent = (await store.Query<WebhookEvent>().ToList())
            .Where(e => (!string.IsNullOrWhiteSpace(runner.InstanceId)
                    && string.Equals(e.InstanceId, runner.InstanceId, StringComparison.OrdinalIgnoreCase))
                || string.Equals(e.AssignedRunnerName, runner.RunnerName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.UpdatedAt)
            .FirstOrDefault();

        var profiles = (await store.Query<RunnerProfile>().ToList()).ToList();
        var profile = profiles.FirstOrDefault(p => p.Id == linkedEvent?.MatchedProfileId)
            ?? ResolveProfileFromRunnerName(profiles, runner.RunnerName);
        if (profile == null)
            return null;

        if (profile.Provider == RunnerProvider.GitHubActions
            && (string.IsNullOrWhiteSpace(profile.ProviderCredentialId)
                || await store.Get<ProviderCredential>(profile.ProviderCredentialId) == null))
        {
            return null;
        }

        return await GetProviderRunnerActivityAsync(store, new RunnerInstance
        {
            Id = string.IsNullOrWhiteSpace(runner.InstanceId) ? runner.RunnerName : runner.InstanceId,
            RunnerName = runner.RunnerName,
            ProfileId = profile.Id,
            WebhookEventId = linkedEvent?.Id,
            ProvisioningMode = "dynamic"
        }, ct);
    }

    /// <summary>
    /// Runner names are "{sanitized profile name}-…". The longest matching profile name wins;
    /// a tie is ambiguous and resolves to nothing.
    /// </summary>
    private static RunnerProfile? ResolveProfileFromRunnerName(IEnumerable<RunnerProfile> profiles, string runnerName)
    {
        var matches = profiles
            .Select(p => (Profile: p, Prefix: RunnerMetadataBuilder.SanitizeRunnerNameComponent(p.Name)))
            .Where(m => !string.IsNullOrWhiteSpace(m.Prefix)
                && runnerName.StartsWith(m.Prefix + "-", StringComparison.OrdinalIgnoreCase))
            .GroupBy(m => m.Prefix!.Length)
            .OrderByDescending(g => g.Key)
            .FirstOrDefault()?
            .ToList();
        return matches is [var only] ? only.Profile : null;
    }

    private async Task<bool> IsPersonalAccountAsync(
        GitHubActionsProvider github,
        ProviderCredential credential,
        string owner,
        CancellationToken ct)
    {
        var key = $"{credential.GitHubApiUrl?.TrimEnd('/')}|{owner}";
        if (_ownerIsPersonalAccount.TryGetValue(key, out var cached))
            return cached;

        try
        {
            var isPersonal = string.Equals(
                await github.GetOwnerAccountTypeAsync(credential, owner, ct),
                "User",
                StringComparison.OrdinalIgnoreCase);
            _ownerIsPersonalAccount[key] = isPersonal;
            return isPersonal;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not look up the GitHub account type of {Owner}", owner);
            return false;
        }
    }

    /// <param name="InstallationId">
    /// The GitHub App installation to authenticate the lookup with. An org lookup has no
    /// repository to pick the installation by, so without this a multi-installation app
    /// would ask with its default installation instead of the event's.
    /// </param>
    private sealed record RunnerLookupScope(string? Repository, string? Owner, ProviderCredential Credential, string? InstallationId);

    private static List<RunnerLookupScope> BuildRunnerLookupScopes(ProviderCredential credential, GitHubCleanupScope eventScope)
    {
        var scopes = new List<RunnerLookupScope>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? repository, string? owner, ProviderCredential scoped, string? installationId = null)
        {
            var key = repository != null ? $"repo:{repository}" : $"org:{owner}";
            if (seen.Add(key))
                scopes.Add(new RunnerLookupScope(repository, owner, scoped, installationId));
        }

        var defaultTarget = GitHubCredentialResolver.ResolveDefaultTarget(credential);
        var eventRepository = GitHubCredentialResolver.NormalizeRepository(eventScope.Repository, defaultTarget?.Owner);
        if (eventRepository?.Split('/', 2) is [var eventOwner, _])
        {
            var scoped = ScopeGitHubCredentialToRepository(credential, eventRepository, eventScope.InstallationId);
            var eventInstallationId = scoped.GitHubAppInstallationId;
            Add(eventRepository, null, scoped, eventInstallationId);
            Add(null, eventOwner, scoped, eventInstallationId);
        }
        else
        {
            eventRepository = null;
        }

        var credentialRepository = defaultTarget?.Repository
            ?? GitHubCredentialResolver.NormalizeRepository(credential.GitHubRepo, credential.GitHubOrg);
        if (credentialRepository?.Split('/', 2) is [_, _])
            Add(credentialRepository, null, ScopeGitHubCredentialToRepository(credential, credentialRepository));

        var defaultOwner = eventRepository == null
            ? defaultTarget?.Owner ?? GitHubCredentialResolver.NormalizeOwner(credential.GitHubOrg)
            : null;
        if (!string.IsNullOrWhiteSpace(defaultOwner))
            Add(null, defaultOwner, ScopeGitHubCredentialToRepository(credential, null));

        return scopes;
    }

    public async Task<int> SweepStaleRegistrationsAsync(
        IDocumentStore store,
        CancellationToken ct = default)
    {
        var githubProvider = _providers.OfType<GitHubActionsProvider>().FirstOrDefault();
        if (githubProvider == null)
            return 0;

        var protectedRunnerNames = BuildGitHubProtectedRunnerNames(
            await store.Query<RunnerInstance>().ToList());

        var credentials = (await store.Query<ProviderCredential>().ToList())
            .Where(c => c.Provider == RunnerProvider.GitHubActions)
            .ToList();

        var recentEvents = (await store.Query<WebhookEvent>().ToList())
            .Where(e =>
                string.Equals(e.Provider, RunnerProvider.GitHubActions.ToString(), StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(e.Repository))
            .ToList();

        var removed = 0;
        foreach (var scopedCredential in BuildGitHubSweepScopes(credentials, recentEvents))
        {
            try
            {
                removed += await githubProvider.RemoveOfflineDynamicRunnersAsync(scopedCredential, protectedRunnerNames, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed stale GitHub runner sweep for scope org={Org} repo={Repo}",
                    scopedCredential.GitHubOrg,
                    scopedCredential.GitHubRepo);
            }
        }

        if (removed > 0)
        {
            _logger.LogInformation("Removed {Count} stale GitHub runner registrations during periodic sweep", removed);
        }

        return removed;
    }

    internal static HashSet<string> BuildGitHubProtectedRunnerNames(IEnumerable<RunnerInstance> instances)
        => instances
            .Where(CapacityPlanningService.IsRunnerRunnerManaged)
            .Select(i => i.RunnerName?.Trim())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

    internal static ProviderCredential ScopeGitHubCredentialToRepository(
        ProviderCredential credential,
        string? repository,
        string? installationId = null)
    {
        var clone = new ProviderCredential
        {
            Id = credential.Id,
            Name = credential.Name,
            Provider = credential.Provider,
            GitHubOrg = credential.GitHubOrg,
            GitHubRepo = credential.GitHubRepo,
            GitHubToken = credential.GitHubToken,
            GitHubApiUrl = credential.GitHubApiUrl,
            GitHubServerUrl = credential.GitHubServerUrl,
            GitHubAuthType = credential.GitHubAuthType,
            GitHubAppId = credential.GitHubAppId,
            GitHubAppPrivateKey = credential.GitHubAppPrivateKey,
            GitHubAppInstallationId = GitHubCredentialResolver.ResolveInstallationId(credential, installationId, repository),
            GitHubAppWebhookSecret = credential.GitHubAppWebhookSecret,
            GitHubAppInstallations = CloneInstallations(credential.GitHubAppInstallations),
            GiteaInstanceUrl = credential.GiteaInstanceUrl,
            GiteaRunnerToken = credential.GiteaRunnerToken,
            AzDoOrgUrl = credential.AzDoOrgUrl,
            AzDoProjectName = credential.AzDoProjectName,
            AzDoPat = credential.AzDoPat,
            AzDoPoolName = credential.AzDoPoolName,
            CreatedAt = credential.CreatedAt,
            UpdatedAt = credential.UpdatedAt
        };

        if (string.IsNullOrWhiteSpace(repository))
            return clone;

        var trimmed = repository.Trim().Trim('/');
        var parts = trimmed.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return clone;

        clone.GitHubOrg = parts[0];
        clone.GitHubRepo = $"{parts[0]}/{parts[1]}";
        return clone;
    }

    internal static IEnumerable<ProviderCredential> BuildGitHubSweepScopes(
        IEnumerable<ProviderCredential> credentials,
        IEnumerable<string> recentRepositories)
        => BuildGitHubSweepScopes(
            credentials,
            recentRepositories.Select(repository => new WebhookEvent
            {
                Provider = RunnerProvider.GitHubActions.ToString(),
                Repository = repository
            }));

    internal static IEnumerable<ProviderCredential> BuildGitHubSweepScopes(
        IEnumerable<ProviderCredential> credentials,
        IEnumerable<WebhookEvent> recentEvents)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var repositoryScopes = recentEvents
            .Where(e => !string.IsNullOrWhiteSpace(e.Repository))
            .GroupBy(e => e.Repository.Trim().Trim('/'), StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Repository = g.Key,
                InstallationId = g
                    .Select(e => e.GitHubInstallationId)
                    .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id))
            })
            .ToList();

        foreach (var credential in credentials)
        {
            if (!GitHubAuthenticationService.HasGitHubApiCredentials(credential)
                && !CanUseEventInstallationId(credential))
            {
                continue;
            }

            foreach (var target in GitHubCredentialResolver.GetConfiguredTargets(credential))
            {
                ProviderCredential scopedCredential;
                string key;
                if (!string.IsNullOrWhiteSpace(target.Repository))
                {
                    scopedCredential = ScopeGitHubCredentialToRepository(credential, target.Repository, target.InstallationId);
                    key = $"repo:{scopedCredential.GitHubRepo}";
                }
                else if (!string.IsNullOrWhiteSpace(target.Owner))
                {
                    scopedCredential = ScopeGitHubCredentialToRepository(credential, null, target.InstallationId);
                    scopedCredential.GitHubOrg = target.Owner;
                    scopedCredential.GitHubRepo = null;
                    key = $"org:{scopedCredential.GitHubOrg}";
                }
                else
                {
                    continue;
                }

                if (seen.Add(key) && GitHubAuthenticationService.HasGitHubApiCredentials(scopedCredential))
                    yield return scopedCredential;
            }

            var targetOwners = GitHubCredentialResolver.GetTargetOwners(credential).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var repoScope in repositoryScopes)
            {
                var owner = repoScope.Repository.Split('/', 2)[0];
                if (targetOwners.Count > 0 && !targetOwners.Contains(owner))
                    continue;

                var repoScoped = ScopeGitHubCredentialToRepository(
                    credential,
                    repoScope.Repository,
                    repoScope.InstallationId);
                var repoKey = $"repo:{repoScoped.GitHubRepo}";
                if (seen.Add(repoKey) && GitHubAuthenticationService.HasGitHubApiCredentials(repoScoped))
                    yield return repoScoped;
            }
        }
    }

    private static List<GitHubAppInstallation> CloneInstallations(IEnumerable<GitHubAppInstallation>? installations)
        => installations?
            .Select(i => new GitHubAppInstallation
            {
                Owner = i.Owner,
                Repository = i.Repository,
                InstallationId = i.InstallationId,
                IsDefault = i.IsDefault
            })
            .ToList() ?? [];

    private static bool CanUseEventInstallationId(ProviderCredential credential) =>
        credential.GitHubAuthType == GitHubAuthType.GitHubApp
        && !string.IsNullOrWhiteSpace(credential.GitHubAppId)
        && !string.IsNullOrWhiteSpace(credential.GitHubAppPrivateKey);

    private sealed record GitHubCleanupScope(string? Repository, string? InstallationId);

    private static async Task<GitHubCleanupScope> ResolveGitHubScopeAsync(IDocumentStore store, RunnerInstance instance)
    {
        if (!string.IsNullOrWhiteSpace(instance.WebhookEventId))
        {
            var evt = await store.Get<WebhookEvent>(instance.WebhookEventId);
            if (!string.IsNullOrWhiteSpace(evt?.Repository))
                return new GitHubCleanupScope(evt.Repository, evt.GitHubInstallationId);
        }

        if (!string.IsNullOrWhiteSpace(instance.JobId))
        {
            var evt = (await store.Query<WebhookEvent>().ToList())
                .Where(x => x.JobId == instance.JobId)
                .OrderByDescending(x => x.UpdatedAt)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(evt?.Repository))
                return new GitHubCleanupScope(evt.Repository, evt.GitHubInstallationId);
        }

        return new GitHubCleanupScope(null, null);
    }
}
