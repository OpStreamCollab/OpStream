using OpStream.Shared.Messages;

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

    // ─── Management surface ──────────────────────────────────────────────────
    // Default throws keep older providers compiling until they implement these.

    /// <summary>
    /// Enumerates persisted documents whose global id starts with the supplied tenant prefix.
    /// Results reflect only what is currently in the store — not active in-memory sessions.
    /// </summary>
    IAsyncEnumerable<DocumentInfo> EnumerateAsync(string tenantPrefix, DocumentQuery query, CancellationToken ct = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement EnumerateAsync. Implement IDocumentStore.EnumerateAsync to enable management endpoints.");

    /// <summary>
    /// Returns a lightweight projection for a single document, or <c>null</c> when no data exists.
    /// </summary>
    Task<DocumentInfo?> GetInfoAsync(string documentId, CancellationToken ct = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement GetInfoAsync.");

    /// <summary>
    /// Removes the document's snapshot and op log entirely.
    /// </summary>
    Task DeleteAsync(string documentId, CancellationToken ct = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement DeleteAsync.");

    /// <summary>
    /// Removes every document whose global id starts with the supplied tenant prefix.
    /// Returns the count of documents removed.
    /// </summary>
    Task<int> DeleteByTenantPrefixAsync(string tenantPrefix, CancellationToken ct = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement DeleteByTenantPrefixAsync.");
}
