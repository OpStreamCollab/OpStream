using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpStream.Server.Storage;

namespace OpStream.Server.Diagnostics.HealthChecks;

/// <summary>
/// Default storage health check registered by <c>AddOpStream()</c>.
/// Reports degraded when the in-memory store is still active (i.e. the user
/// has not wired a persistent provider), healthy otherwise.
///
/// <para>
/// Provider-specific packages (Redis, EF Core, Mongo) ship their own dedicated
/// health checks that probe the underlying connection. Those packages
/// <em>replace</em> this registration via <c>Replace(ServiceDescriptor...)</c>
/// or simply override <see cref="IDocumentStore"/> so this check passes through.
/// </para>
/// </summary>
internal sealed class MemoryStorageHealthCheck(IDocumentStore store) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (store is MemoryDocumentStore)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                "OpStream is using the in-memory store. Data is lost on restart. " +
                "Configure a persistent provider (UseRedisStorage, UseSqlServerStorage, ...) for production."));
        }

        // A persistent provider is wired. If that provider exposed its own probe
        // (e.g. RedisStorageHealthCheck), it is what runs under its own name; this
        // default just confirms the type is registered.
        return Task.FromResult(HealthCheckResult.Healthy(
            $"Storage provider: {store.GetType().Name}"));
    }
}
