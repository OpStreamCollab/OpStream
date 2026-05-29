namespace OpStream.Shared.Messages;

/// <summary>
/// Represents a persisted operation in the log. The payload is kept as raw UTF-8 bytes 
/// at this layer to avoid allocation, and is deserialized only when it reaches the Engine.
/// </summary>
public record StoredOp(
    long Revision,
    string AuthorId,
    DateTimeOffset Timestamp,
    ReadOnlyMemory<byte> Payload,
    string EngineType
);

/// <summary>
/// Represents the condensed state of a document at a specific revision.
/// </summary>
public record DocumentSnapshot(
    long Revision,
    DateTimeOffset Timestamp,
    ReadOnlyMemory<byte> State
);

/// <summary>
/// Represents a significant point in the document's history.
/// </summary>
public record HistoryMilestone(
    long Revision,
    DateTimeOffset Timestamp,
    string? Name
);

/// <summary>
/// Lightweight projection used by management endpoints to enumerate persisted documents.
/// </summary>
public record DocumentInfo(
    string DocumentId,
    long Revision,
    DateTimeOffset LastModified,
    long OpCount
);

/// <summary>
/// Optional filter applied when enumerating documents.
/// </summary>
public record DocumentQuery(
    int? Skip = null,
    int? Take = null
);



/// <summary>
/// Direction of priority when exact concurrent operations conflict.
/// Crucial for OT algorithms to break ties deterministically without breaking convergence.
/// </summary>
public enum TransformPriority
{
    IncomingWins,
    ExistingWins
}
