using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;
using OpStream.Server.Storage;
using OpStream.Server.Storage.Redis;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Options for configuring the Redis document store.
/// </summary>
public sealed class RedisStorageOptions
{
    /// <summary>Redis connection string (required).</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// How often a snapshot is written to Redis.
    /// When null, the global <c>ISnapshotPolicy</c> drives snapshots.
    /// </summary>
    public TimeSpan? SnapshotEvery { get; set; }

    /// <summary>
    /// How long operation log entries are retained in Redis Streams.
    /// When null, entries are never pruned automatically.
    /// </summary>
    public TimeSpan? OpLogRetention { get; set; }
}

/// <summary>
/// Extension methods for configuring the Redis document store.
/// </summary>
public static class RedisOpStreamExtensions
{
    /// <summary>
    /// Replaces the current storage with <see cref="RedisDocumentStore"/>.
    /// Also replaces the <c>opstream-storage</c> health check with a Redis-aware probe.
    /// </summary>
    public static IOpStreamBuilder UseRedisStorage(
        this IOpStreamBuilder builder,
        Action<RedisStorageOptions> configure)
    {
        var options = new RedisStorageOptions();
        configure(options);

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new InvalidOperationException(
                "RedisStorageOptions.ConnectionString must not be empty. " +
                "Set it inside the configure action passed to UseRedisStorage().");

        builder.Services.TryAddSingleton<IConnectionMultiplexer>(
            ConnectionMultiplexer.Connect(options.ConnectionString));

        builder.Services.TryAddSingleton(options);
        builder.Services.TryAddSingleton<RedisDocumentStore>();

        builder.Services.Replace(ServiceDescriptor.Singleton<IDocumentStore>(
            sp => sp.GetRequiredService<RedisDocumentStore>()));
        builder.Services.Replace(ServiceDescriptor.Singleton<IHistoryStore>(
            sp => sp.GetRequiredService<RedisDocumentStore>()));

        // Swap the default opstream-storage check for a Redis ping.
        builder.Services.Configure<HealthCheckServiceOptions>(o =>
        {
            foreach (var stale in o.Registrations.Where(r => r.Name == "opstream-storage").ToList())
                o.Registrations.Remove(stale);
        });
        builder.Services.AddHealthChecks()
            .AddCheck<RedisStorageHealthCheck>(
                name: "opstream-storage",
                failureStatus: HealthStatus.Unhealthy,
                tags: new[] { "opstream", "storage", "redis" });

        return builder;
    }

    /// <summary>
    /// Replaces the current storage with <see cref="RedisDocumentStore"/>
    /// using the supplied connection string and default options.
    /// </summary>
    public static IOpStreamBuilder UseRedisStorage(
        this IOpStreamBuilder builder,
        string connectionString)
        => builder.UseRedisStorage(o => o.ConnectionString = connectionString);


}
