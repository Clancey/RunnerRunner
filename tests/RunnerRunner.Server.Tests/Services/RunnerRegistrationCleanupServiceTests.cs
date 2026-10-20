using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RunnerRunner.Core.Interfaces;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Providers;
using RunnerRunner.Server.Services;
using RunnerRunner.Server.Tests.TestSupport;
using Shiny.DocumentDb;

namespace RunnerRunner.Server.Tests.Services;

public class RunnerRegistrationCleanupServiceTests
{
    [Fact]
    public async Task TryRemoveRunnerAsync_UsesWebhookRepositoryScopeForGitHubCleanup()
    {
        var store = TestDocumentStore.Create();

        var credential = new ProviderCredential
        {
            Id = "cred-1",
            Name = "github",
            Provider = RunnerProvider.GitHubActions,
            GitHubOrg = "redth",
            GitHubToken = "ghp_test"
        };
        await store.Insert(credential);

        var profile = new RunnerProfile
        {
            Id = "profile-1",
            Name = "linux-jit",
            Provider = RunnerProvider.GitHubActions,
            ProviderCredentialId = credential.Id
        };
        await store.Insert(profile);

        await store.Insert(new WebhookEvent
        {
            Id = "evt-1",
            Repository = "poolmath/PoolMath",
            JobId = "job-1",
            Provider = RunnerProvider.GitHubActions.ToString()
        });

        var instance = new RunnerInstance
        {
            Id = "inst-1",
            RunnerName = "linux-jit-1234",
            ProfileId = profile.Id,
            WebhookEventId = "evt-1",
            JobId = "job-1",
            ProvisioningMode = "dynamic"
        };

        var provider = Substitute.For<IRunnerProviderPlugin>();
        provider.Provider.Returns(RunnerProvider.GitHubActions);

        var service = new RunnerRegistrationCleanupService(
            [provider],
            Substitute.For<ILogger<RunnerRegistrationCleanupService>>());

        await service.TryRemoveRunnerAsync(store, instance);

        await provider.Received(1).RemoveRunnerAsync(
            Arg.Is<ProviderCredential>(c =>
                c.GitHubOrg == "poolmath"
                && c.GitHubRepo == "poolmath/PoolMath"),
            instance.RunnerName,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ScopeGitHubCredentialToRepository_PreservesGitHubAppInstallationFromWebhook()
    {
        var credential = new ProviderCredential
        {
            Name = "github-app",
            Provider = RunnerProvider.GitHubActions,
            GitHubOrg = "redth",
            GitHubAuthType = GitHubAuthType.GitHubApp,
            GitHubAppId = "98765",
            GitHubAppInstallationId = "default-installation",
            GitHubAppPrivateKey = "private-key",
            GitHubAppWebhookSecret = "secret"
        };

        var scoped = RunnerRegistrationCleanupService.ScopeGitHubCredentialToRepository(
            credential,
            "poolmath/PoolMath",
            "webhook-installation");

        Assert.Equal("poolmath", scoped.GitHubOrg);
        Assert.Equal("poolmath/PoolMath", scoped.GitHubRepo);
        Assert.Equal("webhook-installation", scoped.GitHubAppInstallationId);
        Assert.Equal("98765", scoped.GitHubAppId);
        Assert.Equal("private-key", scoped.GitHubAppPrivateKey);
        Assert.Equal("secret", scoped.GitHubAppWebhookSecret);
    }

    [Fact]
    public void ScopeGitHubCredentialToRepository_PreservesCredentialWhenRepositoryMissing()
    {
        var credential = new ProviderCredential
        {
            Name = "github",
            Provider = RunnerProvider.GitHubActions,
            GitHubOrg = "redth",
            GitHubRepo = "RunnerRunner"
        };

        var scoped = RunnerRegistrationCleanupService.ScopeGitHubCredentialToRepository(credential, null);

        Assert.Equal("redth", scoped.GitHubOrg);
        Assert.Equal("RunnerRunner", scoped.GitHubRepo);
    }

    [Fact]
    public void BuildGitHubSweepScopes_IncludesRecentReposForMatchingOrg()
    {
        var credential = new ProviderCredential
        {
            Name = "github",
            Provider = RunnerProvider.GitHubActions,
            GitHubOrg = "Redth",
            GitHubToken = "ghp_test"
        };

        var scopes = RunnerRegistrationCleanupService.BuildGitHubSweepScopes(
                [credential],
                ["Redth/ailoha", "PoolMath/PoolMath"])
            .ToList();

        Assert.Contains(scopes, s => s.GitHubOrg == "Redth" && string.IsNullOrWhiteSpace(s.GitHubRepo));
        Assert.Contains(scopes, s => s.GitHubRepo == "Redth/ailoha");
        Assert.DoesNotContain(scopes, s => s.GitHubRepo == "PoolMath/PoolMath");
    }

    [Fact]
    public void BuildGitHubSweepScopes_IncludesGitHubAppInstallationTargets()
    {
        var credential = new ProviderCredential
        {
            Name = "github-app",
            Provider = RunnerProvider.GitHubActions,
            GitHubAuthType = GitHubAuthType.GitHubApp,
            GitHubAppId = "98765",
            GitHubAppPrivateKey = "private-key",
            GitHubAppInstallations =
            [
                new GitHubAppInstallation
                {
                    Owner = "Redth",
                    InstallationId = "111",
                    IsDefault = true
                },
                new GitHubAppInstallation
                {
                    Owner = "PoolMath",
                    Repository = "PoolMath/PoolMath",
                    InstallationId = "222"
                }
            ]
        };

        var scopes = RunnerRegistrationCleanupService.BuildGitHubSweepScopes(
                [credential],
                [
                    new WebhookEvent
                    {
                        Provider = RunnerProvider.GitHubActions.ToString(),
                        Repository = "Redth/ailoha",
                        GitHubInstallationId = "111"
                    },
                    new WebhookEvent
                    {
                        Provider = RunnerProvider.GitHubActions.ToString(),
                        Repository = "PoolMath/PoolMath",
                        GitHubInstallationId = "222"
                    }
                ])
            .ToList();

        Assert.Contains(scopes, s => s.GitHubOrg == "Redth" && s.GitHubAppInstallationId == "111");
        Assert.Contains(scopes, s => s.GitHubRepo == "Redth/ailoha" && s.GitHubAppInstallationId == "111");
        Assert.Contains(scopes, s => s.GitHubRepo == "PoolMath/PoolMath" && s.GitHubAppInstallationId == "222");
    }

    [Fact]
    public void BuildGitHubProtectedRunnerNames_KeepsManagedNamesEvenWhenNotActive()
    {
        var names = RunnerRegistrationCleanupService.BuildGitHubProtectedRunnerNames([
            new RunnerInstance
            {
                RunnerName = "MAUI-Linux-jit-c1e37630",
                HostId = "host-1",
                ProvisioningMode = "dynamic",
                Status = RunnerInstanceStatus.Crashed,
                ManagedByRunnerRunner = true
            },
            new RunnerInstance
            {
                RunnerName = "external-runner",
                HostId = "",
                Status = RunnerInstanceStatus.Running,
                ManagedByRunnerRunner = false
            }
        ]);

        Assert.Contains("MAUI-Linux-jit-c1e37630", names);
        Assert.DoesNotContain("external-runner", names);
    }

    private const string ProbedRunner = "FashionPact-Linux-jit-383f99c0";

    // Clancey/FashionPactPos is a personal-account repository: the runner is registered on
    // the repository and there is no org runner endpoint to fall back to.
    [Theory]
    [InlineData(true, ProviderRunnerActivity.Busy)]
    [InlineData(false, ProviderRunnerActivity.Idle)]
    public async Task GetProviderRunnerActivity_ReadsBusyFromTheRepositoryTheRunnerWasMintedFor(bool busy, ProviderRunnerActivity expected)
    {
        var api = new FakeProviderHttpApi()
            .RespondJson(IsRepoLookup, RunnersJson((ProbedRunner, busy)));
        var (service, store, instance) = await ActivityScenarioAsync(api);

        var activity = await service.GetProviderRunnerActivityAsync(store, instance);

        Assert.Equal(expected, activity);
        var request = Assert.Single(api.Requests);
        Assert.Equal($"/repos/Clancey/FashionPactPos/actions/runners?name={ProbedRunner}&per_page=100", request.PathAndQuery);
    }

    [Fact]
    public async Task GetProviderRunnerActivity_PersonalAccountWithNoRepositoryRegistration_IsNotRegistered()
    {
        var api = new FakeProviderHttpApi()
            .RespondJson(IsRepoLookup, RunnersJson())
            .RespondJson(IsOrgLookup, """{"message":"Not Found"}""", HttpStatusCode.NotFound)
            .RespondJson(IsOwnerLookup, """{"login":"Clancey","type":"User"}""");
        var (service, store, instance) = await ActivityScenarioAsync(api);

        Assert.Equal(ProviderRunnerActivity.NotRegistered, await service.GetProviderRunnerActivityAsync(store, instance));
    }

    // An org can also answer 404 on its runner list (for example when the credential cannot
    // see org runners). Reading that as "no such runner" let a recycle stop a busy org runner.
    [Fact]
    public async Task GetProviderRunnerActivity_OrgRunnerListNotFoundForAnOrganization_IsUnknown()
    {
        var api = new FakeProviderHttpApi()
            .RespondJson(IsRepoLookup, RunnersJson())
            .RespondJson(IsOrgLookup, """{"message":"Not Found"}""", HttpStatusCode.NotFound)
            .RespondJson(IsOwnerLookup, """{"login":"Clancey","type":"Organization"}""");
        var (service, store, instance) = await ActivityScenarioAsync(api);

        Assert.Equal(ProviderRunnerActivity.Unknown, await service.GetProviderRunnerActivityAsync(store, instance));
    }

    [Fact]
    public async Task GetProviderRunnerActivity_OwnerTypeCannotBeLookedUp_IsUnknown()
    {
        var api = new FakeProviderHttpApi()
            .RespondJson(IsRepoLookup, RunnersJson())
            .RespondJson(IsOrgLookup, """{"message":"Not Found"}""", HttpStatusCode.NotFound)
            .RespondJson(IsOwnerLookup, """{"message":"boom"}""", HttpStatusCode.InternalServerError);
        var (service, store, instance) = await ActivityScenarioAsync(api);

        Assert.Equal(ProviderRunnerActivity.Unknown, await service.GetProviderRunnerActivityAsync(store, instance));
    }

    // JitConfigService falls back to the credential's default repository when the event's
    // repository and org reject the registration, so the runner can live there.
    [Fact]
    public async Task GetProviderRunnerActivity_RunnerRegisteredOnTheCredentialsDefaultRepository_IsFound()
    {
        var api = new FakeProviderHttpApi()
            .RespondJson(IsRepoLookup, RunnersJson())
            .RespondJson(IsOrgLookup, """{"message":"Not Found"}""", HttpStatusCode.NotFound)
            .RespondJson(
                request => request.RequestUri!.AbsolutePath == "/repos/Clancey/Other/actions/runners",
                RunnersJson((ProbedRunner, true)));
        var (service, store, instance) = await ActivityScenarioAsync(api, credentialRepo: "Clancey/Other");

        Assert.Equal(ProviderRunnerActivity.Busy, await service.GetProviderRunnerActivityAsync(store, instance));
    }

    [Fact]
    public async Task GetProviderRunnerActivity_OrgRegisteredRunner_IsFoundInTheOrg()
    {
        var api = new FakeProviderHttpApi()
            .RespondJson(IsRepoLookup, RunnersJson())
            .RespondJson(IsOrgLookup, RunnersJson((ProbedRunner, true)));
        var (service, store, instance) = await ActivityScenarioAsync(api);

        Assert.Equal(ProviderRunnerActivity.Busy, await service.GetProviderRunnerActivityAsync(store, instance));
    }

    [Fact]
    public async Task GetProviderRunnerActivity_OnlyAnExactNameMatchCounts()
    {
        var api = new FakeProviderHttpApi()
            .RespondJson(IsRepoLookup, RunnersJson((ProbedRunner + "-old", true)))
            .RespondJson(IsOrgLookup, """{"message":"Not Found"}""", HttpStatusCode.NotFound)
            .RespondJson(IsOwnerLookup, """{"login":"Clancey","type":"User"}""");
        var (service, store, instance) = await ActivityScenarioAsync(api);

        Assert.Equal(ProviderRunnerActivity.NotRegistered, await service.GetProviderRunnerActivityAsync(store, instance));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    public async Task GetProviderRunnerActivity_WhenGitHubCannotBeAsked_IsUnknownNeverIdle(HttpStatusCode status, bool failOrgLookup)
    {
        var api = new FakeProviderHttpApi();
        if (failOrgLookup)
        {
            api.RespondJson(IsRepoLookup, RunnersJson())
                .RespondJson(IsOrgLookup, """{"message":"boom"}""", status);
        }
        else
        {
            api.RespondJson(IsRepoLookup, """{"message":"boom"}""", status);
        }
        var (service, store, instance) = await ActivityScenarioAsync(api);

        Assert.Equal(ProviderRunnerActivity.Unknown, await service.GetProviderRunnerActivityAsync(store, instance));
    }

    [Fact]
    public async Task GetProviderRunnerActivity_CredentialWithABareRepositoryName_ChecksThatRepository()
    {
        var api = new FakeProviderHttpApi()
            .RespondJson(IsRepoLookup, RunnersJson((ProbedRunner, true)));
        var (service, store, instance) = await ActivityScenarioAsync(
            api,
            credentialRepo: "FashionPactPos",
            webhookRepository: null);

        Assert.Equal(ProviderRunnerActivity.Busy, await service.GetProviderRunnerActivityAsync(store, instance));
        Assert.StartsWith("/repos/Clancey/FashionPactPos/actions/runners?", Assert.Single(api.Requests).PathAndQuery);
    }

    [Fact]
    public async Task GetProviderRunnerActivity_NonGitHubProfile_IsNotSupported()
    {
        var store = TestDocumentStore.Create();
        await store.Insert(new RunnerProfile { Id = "azdo", Name = "azdo", Provider = RunnerProvider.AzureDevOps });
        var service = new RunnerRegistrationCleanupService([], NullLogger<RunnerRegistrationCleanupService>.Instance);

        var activity = await service.GetProviderRunnerActivityAsync(
            store,
            new RunnerInstance { Id = "i", RunnerName = "agent-1", ProfileId = "azdo" });

        Assert.Equal(ProviderRunnerActivity.NotSupported, activity);
    }

    // A GitHub App installed on several owners: the org lookup for the event's owner must use
    // the event's installation. It used to fall back to the credential's default installation
    // (another owner), whose token cannot see the Clancey org's runners.
    [Fact]
    public async Task GetProviderRunnerActivity_OrgLookup_UsesTheEventsAppInstallation()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var api = new FakeProviderHttpApi()
            .Respond(
                r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.StartsWith("/app/installations/", StringComparison.Ordinal),
                r =>
                {
                    var id = r.RequestUri!.AbsolutePath.Split('/')[3];
                    var expires = DateTimeOffset.UtcNow.AddMinutes(30).ToString("O");
                    return FakeProviderHttpApi.JsonResponse(
                        "{\"token\":\"inst-" + id + "\",\"expires_at\":\"" + expires + "\"}",
                        HttpStatusCode.Created);
                })
            .RespondJson(IsRepoLookup, RunnersJson())
            .Respond(IsOrgLookup, r => r.Headers.Authorization?.Parameter == "inst-222"
                ? FakeProviderHttpApi.JsonResponse(RunnersJson((ProbedRunner, true)))
                : FakeProviderHttpApi.JsonResponse("{\"message\":\"Not Found\"}", HttpStatusCode.NotFound))
            .RespondJson(IsOwnerLookup, "{\"login\":\"Clancey\",\"type\":\"Organization\"}");
        var store = TestDocumentStore.Create();
        await store.Insert(new ProviderCredential
        {
            Id = "cred-app",
            Name = "multi-app",
            Provider = RunnerProvider.GitHubActions,
            GitHubAuthType = GitHubAuthType.GitHubApp,
            GitHubAppId = "98765",
            GitHubAppPrivateKey = rsa.ExportPkcs8PrivateKeyPem(),
            GitHubAppInstallations =
            [
                new GitHubAppInstallation { Owner = "other-org", InstallationId = "111", IsDefault = true },
                new GitHubAppInstallation { Owner = "Clancey", InstallationId = "222" }
            ]
        });
        await store.Insert(new RunnerProfile
        {
            Id = "fp-linux",
            Name = "FashionPact-Linux",
            Provider = RunnerProvider.GitHubActions,
            ProviderCredentialId = "cred-app"
        });
        await store.Insert(new WebhookEvent
        {
            Id = "evt-118618052121",
            Action = "queued",
            Status = "provisioned",
            Repository = "Clancey/FashionPactPos",
            JobId = "118618052121",
            GitHubInstallationId = "222",
            Provider = RunnerProvider.GitHubActions.ToString()
        });
        var instance = new RunnerInstance
        {
            Id = "inst-383f99c0",
            RunnerName = ProbedRunner,
            ProfileId = "fp-linux",
            WebhookEventId = "evt-118618052121",
            JobId = "118618052121",
            ProvisioningMode = "dynamic"
        };
        var provider = new GitHubActionsProvider(
            api,
            new GitHubAuthenticationService(api, NullLogger<GitHubAuthenticationService>.Instance),
            NullLogger<GitHubActionsProvider>.Instance);
        var service = new RunnerRegistrationCleanupService([provider], NullLogger<RunnerRegistrationCleanupService>.Instance);

        Assert.Equal(ProviderRunnerActivity.Busy, await service.GetProviderRunnerActivityAsync(store, instance));
        var orgLookups = api.Requests.Where(r => r.Uri?.AbsolutePath == "/orgs/Clancey/actions/runners").ToList();
        Assert.NotEmpty(orgLookups);
        Assert.All(orgLookups, r => Assert.Equal("inst-222", r.Authorization?.Parameter));
    }

    private static bool IsRepoLookup(HttpRequestMessage request) =>
        request.Method == HttpMethod.Get
        && request.RequestUri!.AbsolutePath == "/repos/Clancey/FashionPactPos/actions/runners";

    private static bool IsOwnerLookup(HttpRequestMessage request) =>
        request.Method == HttpMethod.Get
        && request.RequestUri!.AbsolutePath == "/users/Clancey";

    private static bool IsOrgLookup(HttpRequestMessage request) =>
        request.Method == HttpMethod.Get
        && request.RequestUri!.AbsolutePath == "/orgs/Clancey/actions/runners";

    private static string RunnersJson(params (string Name, bool Busy)[] runners)
    {
        var items = runners.Select((r, i) =>
            $$"""{"id":{{i + 1}},"name":"{{r.Name}}","os":"Linux","status":"online","busy":{{(r.Busy ? "true" : "false")}}}""");
        return $$"""{"total_count":{{runners.Length}},"runners":[{{string.Join(",", items)}}]}""";
    }

    private static async Task<(RunnerRegistrationCleanupService Service, IDocumentStore Store, RunnerInstance Instance)> ActivityScenarioAsync(
        FakeProviderHttpApi api,
        string? credentialRepo = null,
        string? webhookRepository = "Clancey/FashionPactPos")
    {
        var store = TestDocumentStore.Create();
        await store.Insert(new ProviderCredential
        {
            Id = "cred-gh",
            Name = "github",
            Provider = RunnerProvider.GitHubActions,
            GitHubOrg = "Clancey",
            GitHubRepo = credentialRepo,
            GitHubToken = "ghp_test"
        });
        await store.Insert(new RunnerProfile
        {
            Id = "fp-linux",
            Name = "FashionPact-Linux",
            Provider = RunnerProvider.GitHubActions,
            ProviderCredentialId = "cred-gh"
        });
        await store.Insert(new WebhookEvent
        {
            Id = "evt-118618052121",
            Action = "queued",
            Status = "provisioned",
            Repository = webhookRepository,
            JobId = "118618052121",
            Provider = RunnerProvider.GitHubActions.ToString()
        });

        var instance = new RunnerInstance
        {
            Id = "inst-383f99c0",
            RunnerName = ProbedRunner,
            ProfileId = "fp-linux",
            WebhookEventId = "evt-118618052121",
            JobId = "118618052121",
            ProvisioningMode = "dynamic"
        };

        var provider = new GitHubActionsProvider(
            api,
            new GitHubAuthenticationService(api, NullLogger<GitHubAuthenticationService>.Instance),
            NullLogger<GitHubActionsProvider>.Instance);
        var service = new RunnerRegistrationCleanupService([provider], NullLogger<RunnerRegistrationCleanupService>.Instance);
        return (service, store, instance);
    }
}
