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
    ErrorResponse
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
}

public record JoinRequestData(string DocumentId, string DocumentType, int ClientProtoVersion);
public record JoinResponseData(long Revision, byte[] Snapshot, IEnumerable<AwarenessState> Awareness);
public record OpRequestData(string DocumentId, byte[] Payload, long BaseRevision);
public record OpResponseData(bool Success, long NewRevision, string? ErrorMessage);
public record AwarenessRequestData(string DocumentId, string DataJson);
public record ReceiveOpEventData(byte[] Payload, long NewRevision);
public record ReceiveAwarenessEventData(IEnumerable<AwarenessState> Awareness);
public record PeerDisconnectedEventData(string PeerId);
