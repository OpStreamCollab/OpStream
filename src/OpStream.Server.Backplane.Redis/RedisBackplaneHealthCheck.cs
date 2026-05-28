using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpStream.Shared.Abstractions;

namespace OpStream.Server.Backplane.Redis;

/// <summary>
/// Health check that verifies the Redis connection used by
/// <see cref="RedisBackplane"/> is reachable.
/// </summary>
internal sealed class RedisBackplaneHealthCheck(IBackplane backplane) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (backplane is not RedisBackplane redis)
        {
            return HealthCheckResult.Healthy(
                $"Active backplane is {backplane.GetType().Name}; Redis check skipped.");
        }

        try
        {
            var mp = redis.Multiplexer;
            if (!mp.IsConnected)
                return HealthCheckResult.Unhealthy("Redis backplane multiplexer is not connected.");

            var pong = await mp.GetDatabase().PingAsync().WaitAsync(cancellationToken);
            return HealthCheckResult.Healthy(
                $"Redis backplane reachable (ping={pong.TotalMilliseconds:F1} ms).");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Redis backplane probe failed.", ex);
        }
    }
}
