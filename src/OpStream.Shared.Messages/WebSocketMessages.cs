using System.Text.Json;

namespace OpStream.Shared.Messages;

public enum WebSocketOpMessageType
{
    JoinRequest,
    JoinResponse,
    OpRequest,
    OpResponse,
    AwarenessRequest,
    ReceiveOpEvent,
    ReceiveAwarenessEvent,
    PeerDisconnectedEvent,
    ErrorResponse,

    // Comments
    CreateComment,
    EditComment,
    ResolveComment,
    DeleteComment,
    ListOpenComments,
    ReceiveCommentCreated,
    ReceiveCommentUpdated,
    ReceiveCommentDeleted
}

public class WebSocketMessage
{
    public string? CorrelationId { get; set; }
    public WebSocketOpMessageType MessageType { get; set; }

    // Payload properties (nullable)
    public JoinRequestData? JoinRequest { get; set; }
    public JoinResponseData? JoinResponse { get; set; }
    public OpRequestData? OpRequest { get; set; }
    public OpResponseData? OpResponse { get; set; }
    public AwarenessRequestData? AwarenessRequest { get; set; }
    public ReceiveOpEventData? ReceiveOpEvent { get; set; }
    public ReceiveAwarenessEventData? ReceiveAwarenessEvent { get; set; }
    public PeerDisconnectedEventData? PeerDisconnectedEvent { get; set; }
    public string? ErrorMessage { get; set; }

    // Comment payloads
    public CommentCommandData? CommentCommand { get; set; }
    public object? CommentResponse { get; set; }
    public object? ReceiveCommentCreated { get; set; }
    public object? ReceiveCommentUpdated { get; set; }
    public CommentDeletedData? ReceiveCommentDeleted { get; set; }
}

public record JoinRequestData(string DocumentId, string DocumentType, int ClientProtoVersion);
public record JoinResponseData(long Revision, byte[] Snapshot, IEnumerable<AwarenessState> Awareness);
public record OpRequestData(string DocumentId, byte[] Payload, long BaseRevision);
public record OpResponseData(bool Success, long NewRevision, string? ErrorMessage);
public record AwarenessRequestData(string DocumentId, string DataJson);
public record ReceiveOpEventData(byte[] Payload, long NewRevision);
public record ReceiveAwarenessEventData(IEnumerable<AwarenessState> Awareness);
public record PeerDisconnectedEventData(string PeerId);

// ─── Comment command/response envelopes ──────────────────────────────────────

/// <summary>
/// Carrier for comment commands sent from the client over WebSocket.
/// </summary>
public class CommentCommandData
{
    public string? DocumentId { get; set; }
    public string? CommentId { get; set; }
    public string? Body { get; set; }
    public string? ParentCommentId { get; set; }
    /// <summary>JSON-serialised Anchor object (for CreateComment root comments).</summary>
    public JsonElement? Anchor { get; set; }
}

public record CommentDeletedData(string CommentId);
