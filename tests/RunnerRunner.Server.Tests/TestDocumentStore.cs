using Shiny.DocumentDb;
using Shiny.DocumentDb.Sqlite;
using RunnerRunner.Core.Models;
using RunnerRunner.Server.Models;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Tests;

/// <summary>
/// Creates a fresh in-memory SQLite document store for each test.
/// </summary>
public static class TestDocumentStore
{
    public static IDocumentStore Create()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"rr-test-{Guid.NewGuid():N}.db");
        var store = new SqliteDocumentStore(new DocumentStoreOptions
        {
            DatabaseProvider = new SqliteDatabaseProvider($"Data Source={dbPath}")
        }
        .MapTypeToTable<Host>("hosts")
        .MapTypeToTable<RunnerProfile>("runner_profiles")
        .MapTypeToTable<RunnerInstance>("runner_instances")
        .MapTypeToTable<RunnerAssignment>("runner_assignments")
        .MapTypeToTable<ProvisioningRule>("provisioning_rules")
        .MapTypeToTable<EnvironmentVariableSet>("env_var_sets")
        .MapTypeToTable<RunnerAgentVersion>("runner_agent_versions")
        .MapTypeToTable<ProviderCredential>("provider_credentials")
        .MapTypeToTable<RegistryCredential>("registry_credentials")
        .MapTypeToTable<RunnerInitStepDefinition>("runner_init_steps")
        .MapTypeToTable<RunnerRunnerAuthSettings>("auth_settings")
        .MapTypeToTable<AuditLogEntry>("audit_log")
        .MapTypeToTable<WebhookEvent>("webhook_events"));

        return store;
    }

    /// <summary>
    /// Tables are created on first insert. A live server has long since stored webhook
    /// events, and services that scan them assume the table exists.
    /// </summary>
    public static async Task<IDocumentStore> CreateWithWebhookEventsTableAsync()
    {
        var store = Create();
        await store.Insert(new WebhookEvent { Id = "table-seed", Action = "queued", Status = "completed", JobId = "0" });
        await store.Remove<WebhookEvent>("table-seed");
        return store;
    }
}
