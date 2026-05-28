using System.Collections.Concurrent;
using OpStream.Server.Models;

namespace OpStream.Server.Storage;

/// <summary>
/// In-memory implementation for fast testing and development. Do not use in production.
/// </summary>
public class MemoryDocumentStore : IDocumentStore, IHistoryStore
{
    private readonly ConcurrentDictionary<string, DocumentSnapshot> _snapshots = new();
    
    // Dictionary: DocumentId -> List of immutable operations
    private readonly ConcurrentDictionary<string, List<StoredOp>> _opLogs = new();

    // History Specific
    private readonly ConcurrentDictionary<string, List<StoredOp>> _historyOpLogs = new();
    private readonly ConcurrentDictionary<string, List<NamedSnapshot>> _historySnapshots = new();

    private record NamedSnapshot(DocumentSnapshot Snapshot, string? Name);

    /// <summary>
    /// Loads the latest snapshot of a document.
    /// </summary>
    /// <param name="documentId">The ID of the document.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task representing the load operation, returning the snapshot if found.</returns>
    public Task<DocumentSnapshot?> LoadSnapshotAsync(string documentId, CancellationToken ct = default)
    {
        _snapshots.TryGetValue(documentId, out var snapshot);
        return Task.FromResult(snapshot);
    }

    /// <summary>
    /// Streams all operations for a document since a specific revision.
    /// </summary>
    /// <param name="documentId">The ID of the document.</param>
    /// <param name="sinceRevision">The revision to start streaming from (exclusive).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>An asynchronous enumerable of stored operations.</returns>
    public async IAsyncEnumerable<StoredOp> StreamOpsAsync(string documentId, long sinceRevision, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_opLogs.TryGetValue(documentId, out var log))
        {
            yield break;
        }

        // Returns operations strictly greater than sinceRevision
        List<StoredOp> ops;
        lock (log)
        {
            ops = log.Where(o => o.Revision > sinceRevision).ToList();
        }

        foreach (var op in ops)
        {
            yield return op;
        }
    }

    /// <summary>
    /// Appends a new operation to the document's working log.
    /// </summary>
    /// <param name="documentId">The ID of the document.</param>
    /// <param name="op">The operation to append.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task representing the append operation.</returns>
    public Task AppendOpAsync(string documentId, StoredOp op, CancellationToken ct = default)
    {
        // Hot Storage
        var log = _opLogs.GetOrAdd(documentId, _ => new List<StoredOp>());
        lock (log) 
        {
            log.Add(op);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task AppendHistoryOpAsync(string documentId, StoredOp op, CancellationToken ct = default)
    {
        // Cold Storage
        var historyLog = _historyOpLogs.GetOrAdd(documentId, _ => new List<StoredOp>());
        lock (historyLog)
        {
            historyLog.Add(op);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes a new snapshot for the document.
    /// </summary>
    /// <param name="documentId">The ID of the document.</param>
    /// <param name="snapshot">The snapshot to write.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task representing the write operation.</returns>
    public Task WriteSnapshotAsync(string documentId, DocumentSnapshot snapshot, CancellationToken ct = default)
    {
        _snapshots[documentId] = snapshot;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Compacts the operation history by removing operations up to a specific revision.
    /// </summary>
    /// <param name="documentId">The ID of the document.</param>
    /// <param name="upToRevision">The revision up to which operations should be removed (inclusive).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task representing the compaction operation.</returns>
    public Task CompactAsync(string documentId, long upToRevision, CancellationToken ct = default)
    {
        if (_opLogs.TryGetValue(documentId, out var log))
        {
            lock (log)
            {
                log.RemoveAll(o => o.Revision <= upToRevision);
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<DocumentSnapshot?> LoadHistorySnapshotAsync(string documentId, long maxRevision, CancellationToken ct = default)
    {
        if (!_historySnapshots.TryGetValue(documentId, out var snapshots))
            return Task.FromResult<DocumentSnapshot?>(null);

        lock (snapshots)
        {
            var snapshot = snapshots
                .Where(s => s.Snapshot.Revision <= maxRevision)
                .OrderByDescending(s => s.Snapshot.Revision)
                .Select(s => s.Snapshot)
                .FirstOrDefault();

            return Task.FromResult<DocumentSnapshot?>(snapshot);
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<StoredOp> StreamHistoryOpsAsync(string documentId, long sinceRevision, long upToRevision, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_historyOpLogs.TryGetValue(documentId, out var log))
        {
            yield break;
        }

        List<StoredOp> ops;
        lock (log)
        {
            ops = log.Where(o => o.Revision > sinceRevision && o.Revision <= upToRevision)
                     .OrderBy(o => o.Revision)
                     .ToList();
        }

        foreach (var op in ops)
        {
            yield return op;
        }
    }

    /// <inheritdoc/>
    public Task<IEnumerable<HistoryMilestone>> GetMilestonesAsync(string documentId, CancellationToken ct = default)
    {
        if (!_historySnapshots.TryGetValue(documentId, out var snapshots))
            return Task.FromResult<IEnumerable<HistoryMilestone>>(Enumerable.Empty<HistoryMilestone>());

        lock (snapshots)
        {
            var milestones = snapshots
                .OrderByDescending(s => s.Snapshot.Revision)
                .Select(s => new HistoryMilestone(s.Snapshot.Revision, s.Snapshot.Timestamp, s.Name))
                .ToList();

            return Task.FromResult<IEnumerable<HistoryMilestone>>(milestones);
        }
    }

    /// <inheritdoc/>
    public Task WriteHistorySnapshotAsync(string documentId, DocumentSnapshot snapshot, string? name = null, CancellationToken ct = default)
    {
        var snapshots = _historySnapshots.GetOrAdd(documentId, _ => new List<NamedSnapshot>());
        lock (snapshots)
        {
            snapshots.Add(new NamedSnapshot(snapshot, name));
        }
        return Task.CompletedTask;
    }

    // ─── Management surface ──────────────────────────────────────────────────

    /// <inheritdoc/>
    public async IAsyncEnumerable<DocumentInfo> EnumerateAsync(
        string tenantPrefix,
        DocumentQuery query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Union of snapshot ids and op-log ids — a document may exist in either.
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in _snapshots.Keys) if (k.StartsWith(tenantPrefix, StringComparison.Ordinal)) ids.Add(k);
        foreach (var k in _opLogs.Keys) if (k.StartsWith(tenantPrefix, StringComparison.Ordinal)) ids.Add(k);

        var ordered = ids.OrderBy(id => id, StringComparer.Ordinal);
        if (query.Skip is int skip && skip > 0) ordered = (IOrderedEnumerable<string>)ordered.Skip(skip);
        IEnumerable<string> projected = ordered;
        if (query.Take is int take && take > 0) projected = projected.Take(take);

        foreach (var id in projected)
        {
            ct.ThrowIfCancellationRequested();
            var info = BuildInfo(id);
            if (info is not null) yield return info;
            await Task.Yield();
        }
    }

    /// <inheritdoc/>
    public Task<DocumentInfo?> GetInfoAsync(string documentId, CancellationToken ct = default)
        => Task.FromResult(BuildInfo(documentId));

    private DocumentInfo? BuildInfo(string documentId)
    {
        _snapshots.TryGetValue(documentId, out var snapshot);
        _opLogs.TryGetValue(documentId, out var log);
        if (snapshot is null && log is null) return null;

        long opCount = 0;
        long lastRevision = snapshot?.Revision ?? 0;
        DateTimeOffset lastModified = snapshot?.Timestamp ?? DateTimeOffset.MinValue;

        if (log is not null)
        {
            lock (log)
            {
                opCount = log.Count;
                if (log.Count > 0)
                {
                    var tail = log[^1];
                    if (tail.Revision > lastRevision) lastRevision = tail.Revision;
                    if (tail.Timestamp > lastModified) lastModified = tail.Timestamp;
                }
            }
        }

        return new DocumentInfo(documentId, lastRevision, lastModified, opCount);
    }

    /// <inheritdoc/>
    Task IDocumentStore.DeleteAsync(string documentId, CancellationToken ct)
    {
        _snapshots.TryRemove(documentId, out _);
        _opLogs.TryRemove(documentId, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<int> DeleteByTenantPrefixAsync(string tenantPrefix, CancellationToken ct = default)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in _snapshots.Keys) if (k.StartsWith(tenantPrefix, StringComparison.Ordinal)) keys.Add(k);
        foreach (var k in _opLogs.Keys) if (k.StartsWith(tenantPrefix, StringComparison.Ordinal)) keys.Add(k);

        foreach (var k in keys)
        {
            _snapshots.TryRemove(k, out _);
            _opLogs.TryRemove(k, out _);
        }
        return Task.FromResult(keys.Count);
    }

    /// <inheritdoc/>
    Task IHistoryStore.DeleteAsync(string documentId, CancellationToken ct)
    {
        _historyOpLogs.TryRemove(documentId, out _);
        _historySnapshots.TryRemove(documentId, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task PurgeUpToAsync(string documentId, long upToRevision, CancellationToken ct = default)
    {
        if (_historyOpLogs.TryGetValue(documentId, out var log))
        {
            lock (log) log.RemoveAll(o => o.Revision <= upToRevision);
        }
        if (_historySnapshots.TryGetValue(documentId, out var snaps))
        {
            lock (snaps) snaps.RemoveAll(s => s.Snapshot.Revision <= upToRevision);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    Task<int> IHistoryStore.DeleteByTenantPrefixAsync(string tenantPrefix, CancellationToken ct)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in _historyOpLogs.Keys) if (k.StartsWith(tenantPrefix, StringComparison.Ordinal)) keys.Add(k);
        foreach (var k in _historySnapshots.Keys) if (k.StartsWith(tenantPrefix, StringComparison.Ordinal)) keys.Add(k);

        foreach (var k in keys)
        {
            _historyOpLogs.TryRemove(k, out _);
            _historySnapshots.TryRemove(k, out _);
        }
        return Task.FromResult(keys.Count);
    }
}
