using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpStream.Server.Session;
using OpStream.Server.Transports.SignalR;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for the SignalR transport.
/// </summary>
public static class SignalRTransportExtensions
{
    /// <summary>
    /// Adds the SignalR transport to the OpStream pipeline.
    /// Multiple transports can be active simultaneously — call
    /// <c>AddWebSocketTransport()</c> or <c>AddGrpcTransport()</c> as well if needed.
    /// </summary>
    public static IOpStreamBuilder AddSignalRTransport(this IOpStreamBuilder builder)
    {
        builder.Services.AddSingleton<SignalRBackplaneRelay>();
        return builder;
    }

    /// <inheritdoc cref="AddSignalRTransport"/>
    [Obsolete("Use AddSignalRTransport() instead. This alias will be removed in v1.0.", error: false)]
    public static IOpStreamBuilder AddOpStreamSignalRTransport(this IOpStreamBuilder builder)
        => builder.AddSignalRTransport();

    /// <summary>
    /// Maps the SignalR collaboration hub to <paramref name="pattern"/>.
    /// </summary>
    public static IEndpointConventionBuilder MapOpStreamSignalR(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/collab")
    {
        var router = endpoints.ServiceProvider.GetRequiredService<DocumentRouter>();
        // GetAwaiter().GetResult() is intentional: MapHub is synchronous and the
        // router must be ready before the first client connects.
        router.InitializeAsync().GetAwaiter().GetResult();

        // Ensure the relay singleton is instantiated so it can forward backplane events.
        endpoints.ServiceProvider.GetService<SignalRBackplaneRelay>();

        return endpoints.MapHub<SignalRTransport>(pattern);
    }
}
