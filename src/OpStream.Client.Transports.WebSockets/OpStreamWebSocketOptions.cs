namespace OpStream.Client.Transports.WebSockets;

/// <summary>
/// Configuration options for the WebSocket transport.
/// </summary>
public class OpStreamWebSocketOptions
{
    /// <summary>
    /// Gets or sets the server URI (e.g., ws://localhost:5000/ws-collab).
    /// </summary>
    public string ServerUri { get; set; } = "ws://localhost:5000/ws-collab";
}
