using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpStream.Server.Session;
using OpStream.Server.Transports.WebSockets;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for the raw WebSocket transport.
/// </summary>
public static class WebSocketTransportExtensions
{
    /// <summary>
    /// Adds the WebSocket transport to the OpStream pipeline.
    /// Multiple transports can be active simultaneously — call
    /// <c>AddSignalRTransport()</c> or <c>AddGrpcTransport()</c> as well if needed.
    /// </summary>
    public static IOpStreamBuilder AddWebSocketTransport(this IOpStreamBuilder builder)
    {
        builder.Services.AddSingleton<WebSocketConnectionManager>();
        builder.Services.AddSingleton<WebSocketBackplaneRelay>();
        builder.Services.AddScoped<WebSocketTransport>();
        return builder;
    }

    /// <inheritdoc cref="AddWebSocketTransport"/>
    [Obsolete("Use AddWebSocketTransport() instead. This alias will be removed in v1.0.", error: false)]
    public static IOpStreamBuilder AddOpStreamWebSocketsTransport(this IOpStreamBuilder builder)
        => builder.AddWebSocketTransport();

    /// <summary>
    /// Maps the WebSocket collaboration endpoint to <paramref name="pattern"/>.
    /// </summary>
    public static IEndpointConventionBuilder MapOpStreamWebSockets(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/collab-ws")
    {
        var router = endpoints.ServiceProvider.GetRequiredService<DocumentRouter>();
        router.InitializeAsync().GetAwaiter().GetResult();

        endpoints.ServiceProvider.GetService<WebSocketBackplaneRelay>();

        return endpoints.Map(pattern, async context =>
        {
            var transport = context.RequestServices.GetRequiredService<WebSocketTransport>();
            await transport.HandleAsync(context);
        });
    }
}
