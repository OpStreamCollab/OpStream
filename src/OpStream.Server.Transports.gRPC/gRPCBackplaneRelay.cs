using Google.Protobuf.WellKnownTypes;
using OpStream.Constants;
using OpStream.Server.Session;
using OpStream.Shared.Abstractions;
using OpStream.Shared.Messages;
using OpStream.Shared.Messages.gRPC;
using System.Text.Json;

namespace OpStream.Server.Transports.gRPC;

/// <summary>
/// Relays messages from the backplane to gRPC connected clients.
/// </summary>
public class gRPCBackplaneRelay
{
    private readonly gRPCConnectionManager _connectionManager;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="gRPCBackplaneRelay"/> class.
    /// </summary>
    /// <param name="router">The document router to subscribe to backplane messages.</param>
    /// <param name="connectionManager">The manager for gRPC client connections.</param>
    public gRPCBackplaneRelay(DocumentRouter router, gRPCConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
        router.OnBackplaneMessage += HandleBackplaneMessageAsync;
    }

    /// <summary>
    /// Handles backplane messages and broadcasts them to the appropriate gRPC clients.
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
                    var broadcast = new ServerMessage
                    {
                        ReceiveOpEvent = new ReceiveOpEvent
                        {
                            Payload = Google.Protobuf.ByteString.CopyFrom(opPayload.Operation),
                            NewRevision = opPayload.NewRevision
                        }
                    };
                    await _connectionManager.BroadcastToDocumentAsync(documentId, broadcast, excludePeerId: message.SenderPeerId);
                }
                break;

            case OpStreamConstants.BackplaneMessages.ReceiveAwarenessUpdate:
                var update = JsonSerializer.Deserialize<AwarenessState>(message.Payload.Span, JsonOptions);
                if (update != null)
                {
                    var broadcast = new ServerMessage
                    {
                        ReceiveAwarenessEvent = new ReceiveAwarenessEvent()
                    };
                    broadcast.ReceiveAwarenessEvent.Awareness.Add(ToProto(update));
                    await _connectionManager.BroadcastToDocumentAsync(documentId, broadcast, excludePeerId: message.SenderPeerId);
                }
                break;

            case OpStreamConstants.BackplaneMessages.PeerDisconnected:
                var disconnectBroadcast = new ServerMessage
                {
                    PeerDisconnectedEvent = new PeerDisconnectedEvent { PeerId = message.SenderPeerId ?? "" }
                };
                await _connectionManager.BroadcastToDocumentAsync(documentId, disconnectBroadcast);
                break;
        }
    }

    /// <summary>
    /// Converts an <see cref="AwarenessState"/> object to its gRPC protobuf representation.
    /// </summary>
    /// <param name="state">The awareness state to convert.</param>
    /// <returns>The protobuf representation of the awareness state.</returns>
    private static AwarenessStateProto ToProto(AwarenessState state)
    {
        return new AwarenessStateProto
        {
            PeerId = state.PeerId,
            DataJson = state.Data.GetRawText(),
            LastUpdated = Timestamp.FromDateTimeOffset(state.LastUpdated)
        };
    }
}
