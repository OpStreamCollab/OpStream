using OpStream.Constants;
using OpStream.Server.Session;
using OpStream.Shared.Abstractions;
using OpStream.Shared.Messages;
using System.Text.Json;

namespace OpStream.Server.Transports.WebSockets;

/// <summary>
/// Relays messages from the backplane to WebSocket connected clients.
/// </summary>
public class WebSocketBackplaneRelay
{
    private readonly WebSocketConnectionManager _connectionManager;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="WebSocketBackplaneRelay"/> class.
    /// </summary>
    /// <param name="router">The document router to subscribe to backplane messages.</param>
    /// <param name="connectionManager">The manager for WebSocket client connections.</param>
    public WebSocketBackplaneRelay(DocumentRouter router, WebSocketConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
        router.OnBackplaneMessage += HandleBackplaneMessageAsync;
    }

    /// <summary>
    /// Handles backplane messages and broadcasts them to the appropriate WebSocket clients.
    /// </summary>
    /// <param name="documentId">The ID of the document associated with the message.</param>
    /// <param name="message">The backplane message to process.</param>
    private async Task HandleBackplaneMessageAsync(string documentId, BackplaneMessage message)
    {
        switch (message.Type)
        {
            case OpStreamConstants.BackplaneMessages.OpApplied:
                var opPayload = JsonSerializer.Deserialize<OpAppliedBackplanePayload>(message.Payload.Span, JsonOptions);
                if (opPayload != null)
                {
                    var broadcast = new WebSocketMessage
                    {
                        MessageType = WebSocketOpMessageType.ReceiveOpEvent,
                        ReceiveOpEvent = new ReceiveOpEventData(opPayload.Operation, opPayload.NewRevision)
                    };
                    await _connectionManager.BroadcastToDocumentAsync(documentId, broadcast, excludePeerId: message.SenderPeerId);
                }
                break;

            case OpStreamConstants.BackplaneMessages.ReceiveAwarenessUpdate:
                var update = JsonSerializer.Deserialize<AwarenessState>(message.Payload.Span, JsonOptions);
                if (update != null)
                {
                    var broadcast = new WebSocketMessage
                    {
                        MessageType = WebSocketOpMessageType.ReceiveAwarenessEvent,
                        ReceiveAwarenessEvent = new ReceiveAwarenessEventData(new[] { update })
                    };
                    await _connectionManager.BroadcastToDocumentAsync(documentId, broadcast, excludePeerId: message.SenderPeerId);
                }
                break;

            case OpStreamConstants.BackplaneMessages.PeerDisconnected:
                var disconnectBroadcast = new WebSocketMessage
                {
                    MessageType = WebSocketOpMessageType.PeerDisconnectedEvent,
                    PeerDisconnectedEvent = new PeerDisconnectedEventData(message.SenderPeerId ?? "")
                };
                await _connectionManager.BroadcastToDocumentAsync(documentId, disconnectBroadcast);
                break;
        }
    }
}
