using Microsoft.Extensions.DependencyInjection.Extensions;
using OpStream.Client.Transports;
using OpStream.Client.Transports.WebSockets;

namespace Microsoft.Extensions.DependencyInjection;

public static class WebSocketClientExtensions
{
    /// <summary>
    /// Configures the client to use WebSockets as transport.
    /// </summary>
    public static IOpStreamClientBuilder UseWebSocketTransport(
        this IOpStreamClientBuilder builder,
        Action<OpStreamWebSocketOptions> configureOptions)
    {
        builder.Services.Configure(configureOptions);
        builder.Services.TryAddTransient<IOpStreamClient, WebSocketOpStreamClient>();

        return builder;
    }
}
