using OpStream.Server.Models;

namespace OpStream.Server.Storage;

/// <summary>
/// Specialized store for accessing historical data (Cold Storage).
/// </summary>
public interface IHistoryStore
{
    /// <summary>
    /// Loads the closest historical snapshot before or at the specified revision.
    /// </summary>
    Task<DocumentSnapshot?> LoadHistorySnapshotAsync(string documentId, long maxRevision, CancellationToken ct = default);

    /// <summary>
    /// Streams a range of historical operations.
    /// </summary>
    IAsyncEnumerable<StoredOp> StreamHistoryOpsAsync(string documentId, long sinceRevision, long upToRevision, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a list of important milestones for a document's timeline.
    /// </summary>
    Task<IEnumerable<HistoryMilestone>> GetMilestonesAsync(string documentId, CancellationToken ct = default);

    /// <summary>
    /// Persists a named historical snapshot.
    /// </summary>
    Task WriteHistorySnapshotAsync(string documentId, DocumentSnapshot snapshot, string? name = null, CancellationToken ct = default);

    /// <summary>
    /// Appends a new operation to the immutable historical log.
    /// </summary>
    Task AppendHistoryOpAsync(string documentId, StoredOp op, CancellationToken ct = default);

    // ─── Management surface ──────────────────────────────────────────────────

    /// <summary>
    /// Removes the document's historical snapshots and op log entirely.
    /// </summary>
    Task DeleteAsync(string documentId, CancellationToken ct = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement DeleteAsync.");

    /// <summary>
    /// Removes historical operations and snapshots up to the specified revision (inclusive).
    /// </summary>
    Task PurgeUpToAsync(string documentId, long upToRevision, CancellationToken ct = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement PurgeUpToAsync.");

    /// <summary>
    /// Removes every history record whose document id starts with the supplied tenant prefix.
    /// </summary>
    Task<int> DeleteByTenantPrefixAsync(string tenantPrefix, CancellationToken ct = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement DeleteByTenantPrefixAsync.");
}
