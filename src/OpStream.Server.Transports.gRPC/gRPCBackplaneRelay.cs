using Google.Protobuf.WellKnownTypes;
using OpStream.Constants;
using OpStream.Server.Comments;
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
    private readonly gRPCCommentConnectionManager _commentConnections;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public gRPCBackplaneRelay(
        DocumentRouter router,
        gRPCConnectionManager connectionManager,
        gRPCCommentConnectionManager commentConnections)
    {
        _connectionManager = connectionManager;
        _commentConnections = commentConnections;
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

            case OpStreamConstants.BackplaneMessages.CommentCreated:
            {
                var comment = JsonSerializer.Deserialize<Comment>(message.Payload.Span, JsonOptions);
                if (comment is not null)
                    await _commentConnections.BroadcastAsync(documentId, new CommentEvent
                    {
                        Created = gRPCCommentsTransport.ToProto(comment)
                    });
                break;
            }

            case OpStreamConstants.BackplaneMessages.CommentUpdated:
            {
                var comment = JsonSerializer.Deserialize<Comment>(message.Payload.Span, JsonOptions);
                if (comment is not null)
                    await _commentConnections.BroadcastAsync(documentId, new CommentEvent
                    {
                        Updated = gRPCCommentsTransport.ToProto(comment)
                    });
                break;
            }

            case OpStreamConstants.BackplaneMessages.CommentDeleted:
            {
                var deleted = JsonSerializer.Deserialize<DeletedCommentPayload>(message.Payload.Span, JsonOptions);
                if (deleted is not null)
                    await _commentConnections.BroadcastAsync(documentId, new CommentEvent
                    {
                        DeletedCommentId = deleted.CommentId
                    });
                break;
            }
        }
    }

    private record DeletedCommentPayload(string CommentId, string DocumentId);

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
