using OpStream.Server.Models;

namespace OpStream.Server.Storage;

/// <summary>
/// Contract for long-term document persistence.
/// </summary>
public interface IDocumentStore
{
    /// <summary>
    /// Retrieves the latest available document snapshot for fast cold-starts.
    /// </summary>
    Task<DocumentSnapshot?> LoadSnapshotAsync(string documentId, CancellationToken ct = default);

    /// <summary>
    /// Returns a continuous stream of operations that occurred after the specified revision.
    /// </summary>
    IAsyncEnumerable<StoredOp> StreamOpsAsync(string documentId, long sinceRevision, CancellationToken ct = default);

    /// <summary>
    /// Appends a new operation to the end of the immutable log.
    /// </summary>
    Task AppendOpAsync(string documentId, StoredOp op, CancellationToken ct = default);

    /// <summary>
    /// Writes a new snapshot to accelerate future loads.
    /// </summary>
    Task WriteSnapshotAsync(string documentId, DocumentSnapshot snapshot, CancellationToken ct = default);

    /// <summary>
    /// Deletes old operations from the database up to the specified revision 
    /// (usually after a successful Snapshot write).
    /// </summary>
    Task CompactAsync(string documentId, long upToRevision, CancellationToken ct = default);
}
