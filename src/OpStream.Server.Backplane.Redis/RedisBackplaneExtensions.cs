using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpStream.Server.Session;
using OpStream.Shared.Abstractions;

namespace OpStream.Server.Backplane.Redis;

/// <summary>
/// Options for configuring the Redis backplane.
/// </summary>
public sealed class RedisBackplaneOptions
{
    /// <summary>Redis connection string (required).</summary>
    public string ConnectionString { get; set; } = string.Empty;
}

/// <summary>
/// Extension methods for configuring the Redis backplane.
/// </summary>
public static class RedisBackplaneExtensions
{
    /// <summary>
    /// Replaces the default <c>LocalBackplane</c> with <see cref="RedisBackplane"/>.
    /// Also replaces the <c>opstream-backplane</c> health check with a Redis-aware probe.
    /// </summary>
    public static IOpStreamBuilder UseRedisBackplane(
        this IOpStreamBuilder builder,
        Action<RedisBackplaneOptions> configure)
    {
        var options = new RedisBackplaneOptions();
        configure(options);

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new InvalidOperationException(
                "RedisBackplaneOptions.ConnectionString must not be empty. " +
                "Set it inside the configure action passed to UseRedisBackplane().");

        builder.Services.Replace(ServiceDescriptor.Singleton<IBackplane>(
            _ => new RedisBackplane(options.ConnectionString)));

        builder.Services.Replace(ServiceDescriptor.Singleton<IDocumentOwnershipManager>(
            _ => new RedisDocumentOwnershipManager(options.ConnectionString)));

        // Swap the default opstream-backplane check for a Redis ping.
        builder.Services.Configure<HealthCheckServiceOptions>(o =>
        {
            foreach (var stale in o.Registrations.Where(r => r.Name == "opstream-backplane").ToList())
                o.Registrations.Remove(stale);
        });
        builder.Services.AddHealthChecks()
            .AddCheck<RedisBackplaneHealthCheck>(
                name: "opstream-backplane",
                failureStatus: HealthStatus.Unhealthy,
                tags: new[] { "opstream", "backplane", "redis" });

        return builder;
    }

    /// <summary>
    /// Replaces the default <c>LocalBackplane</c> with <see cref="RedisBackplane"/>
    /// using the supplied connection string and default options.
    /// </summary>
    public static IOpStreamBuilder UseRedisBackplane(
        this IOpStreamBuilder builder,
        string connectionString)
        => builder.UseRedisBackplane(o => o.ConnectionString = connectionString);
}
