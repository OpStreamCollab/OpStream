using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpStream.Server.Session;
using OpStream.Shared.Abstractions;

namespace OpStream.Server.Diagnostics.HealthChecks;

/// <summary>
/// Reports the state of the registered <see cref="IBackplane"/>.
/// The default <see cref="LocalBackplane"/> is reported as healthy because it
/// is a deliberate single-node configuration — but operators see in the name
/// that no cross-node fan-out is happening.
/// </summary>
internal sealed class LocalBackplaneHealthCheck(IBackplane backplane) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (backplane is LocalBackplane)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                "Single-node mode (LocalBackplane)."));
        }

        // A real backplane registered its own dedicated health check;
        // we just confirm the type is wired.
        return Task.FromResult(HealthCheckResult.Healthy(
            $"Backplane registered: {backplane.GetType().Name}"));
    }
}
