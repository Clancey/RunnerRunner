using System.Linq.Expressions;
using System.Text.Json;
using Shiny.DocumentDb;
using Host = RunnerRunner.Core.Models.Host;

namespace RunnerRunner.Server.Services;

internal static class HostDocumentUpdates
{
    // Write only the fields owned by the caller, never the rest of its possibly stale snapshot.
    public static Task UpdateAsync(
        IDocumentStore store,
        Host host,
        params Expression<Func<Host, object?>>[] properties)
        => store.RunInTransaction(async transaction =>
        {
            foreach (var property in properties)
            {
                // DocumentDB 3.x formats non-scalar values with ToString(), which is
                // not JSON for dates, enums or collections. Normalize them first.
                var json = JsonSerializer.SerializeToElement(property.Compile(preferInterpretation: true)(host));
                object? value = json.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.String => json.GetString(),
                    JsonValueKind.True or JsonValueKind.False => json.GetBoolean(),
                    _ => json
                };
                if (!await transaction.SetProperty(host.Id, property, value))
                    throw new InvalidOperationException($"Host '{host.Id}' was not found.");
            }
        });
}
