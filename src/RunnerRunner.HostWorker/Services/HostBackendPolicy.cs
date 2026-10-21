using RunnerRunner.Core.Models;

namespace RunnerRunner.HostWorker.Services;

internal static class HostBackendPolicy
{
    internal static HashSet<ExecutionBackend>? Read(IConfiguration configuration)
    {
        var section = configuration.GetSection("HostWorker:AllowedBackends");
        if (!section.Exists())
            return null;
        var values = section.Get<string[]>() ?? [];
        var allowed = new HashSet<ExecutionBackend>();
        foreach (var value in values)
        {
            if (!Enum.TryParse<ExecutionBackend>(value, true, out var backend)
                || !Enum.IsDefined(backend))
                throw new InvalidOperationException($"Unknown allowed HostWorker backend '{value}'.");
            allowed.Add(backend);
        }
        if (allowed.Count == 0)
            throw new InvalidOperationException("HostWorker:AllowedBackends must contain at least one backend when configured.");
        return allowed;
    }

    internal static bool Allows(IConfiguration configuration, ExecutionBackend backend) =>
        Read(configuration)?.Contains(backend) ?? true;

    internal static IReadOnlyCollection<string> Filter(IConfiguration configuration, IReadOnlyCollection<string> capabilities)
    {
        var allowed = Read(configuration);
        return allowed == null ? capabilities
            : capabilities.Where(capability => allowed.Any(backend =>
                backend.ToString().Equals(capability, StringComparison.OrdinalIgnoreCase))).ToList();
    }
}
