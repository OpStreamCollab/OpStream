namespace OpStream.Client.Transports.WebSockets;

/// <summary>
/// Configuration options for the WebSocket transport.
/// </summary>
public class OpStreamWebSocketOptions
{
    /// <summary>
    /// Gets or sets the server URI (e.g., ws://hostdemo.opstream.stream/ws-collab).
    /// </summary>
    public string ServerUri { get; set; } = "ws://hostdemo.opstream.stream/ws-collab";
}
