using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace OpStream.Server.Storage.Redis;

/// <summary>
/// Health check that verifies the Redis connection used by
/// <see cref="RedisDocumentStore"/> is reachable.
/// </summary>
internal sealed class RedisStorageHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!redis.IsConnected)
            return HealthCheckResult.Unhealthy("Redis multiplexer is not connected.");

        try
        {
            // PING is the canonical lightweight probe.
            var pong = await redis.GetDatabase().PingAsync().WaitAsync(cancellationToken);
            return HealthCheckResult.Healthy(
                $"Redis storage reachable (ping={pong.TotalMilliseconds:F1} ms).");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Redis storage probe failed.", ex);
        }
    }
}
