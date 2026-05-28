using Microsoft.EntityFrameworkCore;
using OpStream.Server.Models;
using OpStream.Server.Storage;

namespace OpStream.Server.Storage.EntityFrameworkCore;

/// <summary>
/// EF Core implementation of the document store, supporting multiple relational databases.
/// </summary>
/// <typeparam name="TContext">The DbContext type that contains the OpStream entities.</typeparam>
public class EfCoreDocumentStore<TContext> : IDocumentStore, IHistoryStore where TContext : DbContext
{
    private readonly IDbContextFactory<TContext> _contextFactory;

    /// <summary>
    /// Initializes a new instance of the EfCoreDocumentStore.
    /// We use a factory because SignalR hubs are long-lived, while DbContexts should be scoped/short-lived.
    /// </summary>
    public EfCoreDocumentStore(IDbContextFactory<TContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// Loads a snapshot for a document.
    /// </summary>
    /// <param name="documentId">The unique identifier of the document.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A document snapshot if found; otherwise, null.</returns>
    public async Task<DocumentSnapshot?> LoadSnapshotAsync(string documentId, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        var entity = await db.Set<DocumentSnapshotEntity>()
                             .FindAsync(new object[] { documentId }, ct);

        if (entity == null) return null;

        return new DocumentSnapshot(entity.Revision, entity.Timestamp, entity.State);
    }

    /// <summary>
    /// Streams document operations starting from a specific revision.
    /// </summary>
    /// <param name="documentId">The unique identifier of the document.</param>
    /// <param name="sinceRevision">The revision to start streaming from (exclusive).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>An async enumerable of stored operations.</returns>
    public async IAsyncEnumerable<StoredOp> StreamOpsAsync(string documentId, long sinceRevision, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        
        var query = db.Set<DocumentOpEntity>()
                      .AsNoTracking()
                      .Where(o => o.DocumentId == documentId && o.Revision > sinceRevision)
                      .OrderBy(o => o.Revision)
                      .AsAsyncEnumerable();

        await foreach (var entity in query.WithCancellation(ct))
        {
            yield return new StoredOp(
                entity.Revision, entity.AuthorId, entity.Timestamp, 
                entity.Payload, entity.EngineType);
        }
    }

    /// <summary>
    /// Appends a new operation to a document's working log.
    /// </summary>
    /// <param name="documentId">The unique identifier of the document.</param>
    /// <param name="op">The operation to append.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task AppendOpAsync(string documentId, StoredOp op, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        
        var payload = op.Payload.ToArray();

        // Hot Storage (Log de Trabajo)
        var hotEntity = new DocumentOpEntity
        {
            DocumentId = documentId,
            Revision = op.Revision,
            AuthorId = op.AuthorId,
            Timestamp = op.Timestamp,
            Payload = payload,
            EngineType = op.EngineType
        };

        db.Set<DocumentOpEntity>().Add(hotEntity);
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task AppendHistoryOpAsync(string documentId, StoredOp op, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        var payload = op.Payload.ToArray();

        // Cold Storage (Log Histórico Inmutable)
        var coldEntity = new HistoryOpEntity
        {
            DocumentId = documentId,
            Revision = op.Revision,
            AuthorId = op.AuthorId,
            Timestamp = op.Timestamp,
            Payload = payload,
            EngineType = op.EngineType
        };

        db.Set<HistoryOpEntity>().Add(coldEntity);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Writes a snapshot for a document.
    /// </summary>
    /// <param name="documentId">The unique identifier of the document.</param>
    /// <param name="snapshot">The snapshot to write.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task WriteSnapshotAsync(string documentId, DocumentSnapshot snapshot, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        
        var entity = await db.Set<DocumentSnapshotEntity>().FindAsync(new object[] { documentId }, ct);
        if (entity == null)
        {
            entity = new DocumentSnapshotEntity { DocumentId = documentId };
            db.Set<DocumentSnapshotEntity>().Add(entity);
        }

        entity.Revision = snapshot.Revision;
        entity.Timestamp = snapshot.Timestamp;
        entity.State = snapshot.State.ToArray();

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Compacts the operation log for a document up to a specific revision.
    /// </summary>
    /// <param name="documentId">The unique identifier of the document.</param>
    /// <param name="upToRevision">The revision up to which operations should be removed.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task CompactAsync(string documentId, long upToRevision, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        
        await db.Set<DocumentOpEntity>()
                .Where(o => o.DocumentId == documentId && o.Revision <= upToRevision)
                .ExecuteDeleteAsync(ct); // High-performance delete introduced in EF Core 7+
    }

    /// <inheritdoc/>
    public async Task<DocumentSnapshot?> LoadHistorySnapshotAsync(string documentId, long maxRevision, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        
        var entity = await db.Set<HistorySnapshotEntity>()
                             .AsNoTracking()
                             .Where(s => s.DocumentId == documentId && s.Revision <= maxRevision)
                             .OrderByDescending(s => s.Revision)
                             .FirstOrDefaultAsync(ct);

        if (entity == null) return null;

        return new DocumentSnapshot(entity.Revision, entity.Timestamp, entity.State);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<StoredOp> StreamHistoryOpsAsync(string documentId, long sinceRevision, long upToRevision, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        
        var query = db.Set<HistoryOpEntity>()
                      .AsNoTracking()
                      .Where(o => o.DocumentId == documentId && o.Revision > sinceRevision && o.Revision <= upToRevision)
                      .OrderBy(o => o.Revision)
                      .AsAsyncEnumerable();

        await foreach (var entity in query.WithCancellation(ct))
        {
            yield return new StoredOp(
                entity.Revision, entity.AuthorId, entity.Timestamp, 
                entity.Payload, entity.EngineType);
        }
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<HistoryMilestone>> GetMilestonesAsync(string documentId, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        
        return await db.Set<HistorySnapshotEntity>()
                       .AsNoTracking()
                       .Where(s => s.DocumentId == documentId)
                       .OrderByDescending(s => s.Revision)
                       .Select(s => new HistoryMilestone(s.Revision, s.Timestamp, s.Name))
                       .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task WriteHistorySnapshotAsync(string documentId, DocumentSnapshot snapshot, string? name = null, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        
        var entity = new HistorySnapshotEntity
        {
            DocumentId = documentId,
            Revision = snapshot.Revision,
            Timestamp = snapshot.Timestamp,
            State = snapshot.State.ToArray(),
            Name = name
        };

        db.Set<HistorySnapshotEntity>().Add(entity);
        await db.SaveChangesAsync(ct);
    }
}
