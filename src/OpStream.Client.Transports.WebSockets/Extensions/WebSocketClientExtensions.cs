using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpStream.Client.Transports;
using OpStream.Client.Transports.WebSockets;

namespace Microsoft.Extensions.DependencyInjection;

public static class WebSocketClientExtensions
{
    /// <summary>
    /// Configures the collaboration client to use WebSockets as transport.
    /// </summary>
    public static IOpStreamClientBuilder UseWebSocketTransport(
        this IOpStreamClientBuilder builder,
        Action<OpStreamWebSocketOptions> configureOptions)
    {
        builder.Services.Configure(configureOptions);
        builder.Services.TryAddTransient<IOpStreamClient, WebSocketOpStreamClient>();
        return builder;
    }

    /// <summary>
    /// Registers <see cref="WebSocketOpStreamManagementClient"/> as <see cref="IOpStreamManagementClient"/>.
    /// </summary>
    public static IOpStreamClientBuilder UseWebSocketManagementTransport(
        this IOpStreamClientBuilder builder,
        Action<OpStreamWebSocketManagementOptions> configureOptions)
    {
        builder.Services.Configure(configureOptions);
        builder.Services.TryAddTransient<IOpStreamManagementClient>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<OpStreamWebSocketManagementOptions>>().Value;
            return new WebSocketOpStreamManagementClient(opts.ManagementWsUri, opts.VersioningWsUri);
        });
        return builder;
    }
}

/// <summary>Configuration options for the WebSocket management transport.</summary>
public class OpStreamWebSocketManagementOptions
{
    /// <summary>WebSocket URI for the management endpoint.</summary>
    public string ManagementWsUri { get; set; } = "ws://hostdemo.opstream.stream/ws-mgmt";

    /// <summary>WebSocket URI for the versioning endpoint.</summary>
    public string VersioningWsUri { get; set; } = "ws://hostdemo.opstream.stream/ws-versioning";
}
