using MongoDB.Bson;
using MongoDB.Driver;
using OpStream.Server.Storage;
using OpStream.Shared.Messages;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace OpStream.Server.Storage.MongoDB;

/// <summary>
/// MongoDB implementation of the document and history store.
/// </summary>
public class MongoDocumentStore : IDocumentStore, IHistoryStore
{
    private readonly IMongoDatabase _database;
    private readonly IMongoCollection<MongoDocumentOp> _ops;
    private readonly IMongoCollection<MongoDocumentSnapshot> _snapshots;
    private readonly IMongoCollection<MongoHistoryOp> _historyOps;
    private readonly IMongoCollection<MongoHistorySnapshot> _historySnapshots;

    public MongoDocumentStore(IMongoDatabase database)
    {
        _database = database;
        _ops = _database.GetCollection<MongoDocumentOp>("ops");
        _snapshots = _database.GetCollection<MongoDocumentSnapshot>("snapshots");
        _historyOps = _database.GetCollection<MongoHistoryOp>("history_ops");
        _historySnapshots = _database.GetCollection<MongoHistorySnapshot>("history_snapshots");

        // Ensure indexes
        _ops.Indexes.CreateMany([
            new CreateIndexModel<MongoDocumentOp>(Builders<MongoDocumentOp>.IndexKeys.Ascending(x => x.DocumentId).Ascending(x => x.Revision)),
        ]);

        _historyOps.Indexes.CreateMany([
            new CreateIndexModel<MongoHistoryOp>(Builders<MongoHistoryOp>.IndexKeys.Ascending(x => x.DocumentId).Ascending(x => x.Revision)),
        ]);

        _historySnapshots.Indexes.CreateMany([
            new CreateIndexModel<MongoHistorySnapshot>(Builders<MongoHistorySnapshot>.IndexKeys.Ascending(x => x.DocumentId).Descending(x => x.Revision)),
        ]);
    }

    public async Task<DocumentSnapshot?> LoadSnapshotAsync(string documentId, CancellationToken ct = default)
    {
        var filter = Builders<MongoDocumentSnapshot>.Filter.Eq(x => x.DocumentId, documentId);
        var entity = await _snapshots.Find(filter).FirstOrDefaultAsync(ct);
        if (entity == null) return null;

        return new DocumentSnapshot(entity.Revision, entity.Timestamp, entity.State);
    }

    public async IAsyncEnumerable<StoredOp> StreamOpsAsync(string documentId, long sinceRevision, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var filter = Builders<MongoDocumentOp>.Filter.Eq(x => x.DocumentId, documentId) &
                     Builders<MongoDocumentOp>.Filter.Gt(x => x.Revision, sinceRevision);

        using var cursor = await _ops.Find(filter).SortBy(x => x.Revision).ToCursorAsync(ct);

        while (await cursor.MoveNextAsync(ct))
        {
            foreach (var entity in cursor.Current)
            {
                yield return new StoredOp(entity.Revision, entity.AuthorId, entity.Timestamp, entity.Payload, entity.EngineType);
            }
        }
    }

    public async Task AppendOpAsync(string documentId, StoredOp op, CancellationToken ct = default)
    {
        var entity = new MongoDocumentOp
        {
            Id = $"{documentId}:{op.Revision}",
            DocumentId = documentId,
            Revision = op.Revision,
            AuthorId = op.AuthorId,
            Timestamp = op.Timestamp.UtcDateTime,
            Payload = op.Payload.ToArray(),
            EngineType = op.EngineType
        };

        await _ops.InsertOneAsync(entity, null, ct);
    }

    public async Task WriteSnapshotAsync(string documentId, DocumentSnapshot snapshot, CancellationToken ct = default)
    {
        var filter = Builders<MongoDocumentSnapshot>.Filter.Eq(x => x.DocumentId, documentId);
        var update = Builders<MongoDocumentSnapshot>.Update
            .Set(x => x.Revision, snapshot.Revision)
            .Set(x => x.Timestamp, snapshot.Timestamp.UtcDateTime)
            .Set(x => x.State, snapshot.State.ToArray());

        await _snapshots.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct);
    }

    public async Task CompactAsync(string documentId, long upToRevision, CancellationToken ct = default)
    {
        var filter = Builders<MongoDocumentOp>.Filter.Eq(x => x.DocumentId, documentId) &
                     Builders<MongoDocumentOp>.Filter.Lte(x => x.Revision, upToRevision);

        await _ops.DeleteManyAsync(filter, ct);
    }

    public async Task<DocumentSnapshot?> LoadHistorySnapshotAsync(string documentId, long maxRevision, CancellationToken ct = default)
    {
        var filter = Builders<MongoHistorySnapshot>.Filter.Eq(x => x.DocumentId, documentId) &
                     Builders<MongoHistorySnapshot>.Filter.Lte(x => x.Revision, maxRevision);

        var entity = await _historySnapshots.Find(filter).SortByDescending(x => x.Revision).FirstOrDefaultAsync(ct);
        if (entity == null) return null;

        return new DocumentSnapshot(entity.Revision, entity.Timestamp, entity.State);
    }

    public async IAsyncEnumerable<StoredOp> StreamHistoryOpsAsync(string documentId, long sinceRevision, long upToRevision, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var filter = Builders<MongoHistoryOp>.Filter.Eq(x => x.DocumentId, documentId) &
                     Builders<MongoHistoryOp>.Filter.Gt(x => x.Revision, sinceRevision) &
                     Builders<MongoHistoryOp>.Filter.Lte(x => x.Revision, upToRevision);

        using var cursor = await _historyOps.Find(filter).SortBy(x => x.Revision).ToCursorAsync(ct);

        while (await cursor.MoveNextAsync(ct))
        {
            foreach (var entity in cursor.Current)
            {
                yield return new StoredOp(entity.Revision, entity.AuthorId, entity.Timestamp, entity.Payload, entity.EngineType);
            }
        }
    }

    public async Task<IEnumerable<HistoryMilestone>> GetMilestonesAsync(string documentId, CancellationToken ct = default)
    {
        var filter = Builders<MongoHistorySnapshot>.Filter.Eq(x => x.DocumentId, documentId);
        var projections = Builders<MongoHistorySnapshot>.Projection.Expression(s => new HistoryMilestone(s.Revision, s.Timestamp, s.Name));

        return await _historySnapshots.Find(filter).SortByDescending(x => x.Revision).Project(projections).ToListAsync(ct);
    }

    public async Task WriteHistorySnapshotAsync(string documentId, DocumentSnapshot snapshot, string? name = null, CancellationToken ct = default)
    {
        var entity = new MongoHistorySnapshot
        {
            DocumentId = documentId,
            Revision = snapshot.Revision,
            Timestamp = snapshot.Timestamp.UtcDateTime,
            State = snapshot.State.ToArray(),
            Name = name
        };

        await _historySnapshots.InsertOneAsync(entity, null, ct);
    }

    public async Task AppendHistoryOpAsync(string documentId, StoredOp op, CancellationToken ct = default)
    {
        var entity = new MongoHistoryOp
        {
            Id = $"{documentId}:{op.Revision}",
            DocumentId = documentId,
            Revision = op.Revision,
            AuthorId = op.AuthorId,
            Timestamp = op.Timestamp.UtcDateTime,
            Payload = op.Payload.ToArray(),
            EngineType = op.EngineType
        };

        await _historyOps.InsertOneAsync(entity, null, ct);
    }

    // ─── Management surface ──────────────────────────────────────────────────

    private static BsonRegularExpression PrefixRegex(string prefix)
        => new($"^{Regex.Escape(prefix)}");

    public async IAsyncEnumerable<DocumentInfo> EnumerateAsync(
        string tenantPrefix,
        DocumentQuery query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var regex = PrefixRegex(tenantPrefix);

        // Aggregate ops per document (single round-trip, index-friendly because the match
        // stage uses an anchored regex on DocumentId).
        var opsAggCursor = await _ops.Aggregate()
            .Match(Builders<MongoDocumentOp>.Filter.Regex(x => x.DocumentId, regex))
            .Group(x => x.DocumentId, g => new
            {
                DocumentId = g.Key,
                MaxRevision = g.Max(x => x.Revision),
                MaxTimestamp = g.Max(x => x.Timestamp),
                OpCount = g.LongCount()
            })
            .ToListAsync(ct);

        var snapshots = await _snapshots
            .Find(Builders<MongoDocumentSnapshot>.Filter.Regex(x => x.DocumentId, regex))
            .Project(s => new { s.DocumentId, s.Revision, s.Timestamp })
            .ToListAsync(ct);

        var merged = new Dictionary<string, DocumentInfo>(StringComparer.Ordinal);
        foreach (var o in opsAggCursor)
        {
            merged[o.DocumentId] = new DocumentInfo(
                o.DocumentId,
                o.MaxRevision,
                new DateTimeOffset(DateTime.SpecifyKind(o.MaxTimestamp, DateTimeKind.Utc)),
                o.OpCount);
        }

        foreach (var s in snapshots)
        {
            var snapTs = new DateTimeOffset(DateTime.SpecifyKind(s.Timestamp, DateTimeKind.Utc));
            if (merged.TryGetValue(s.DocumentId, out var existing))
            {
                var rev = Math.Max(existing.Revision, s.Revision);
                var ts = existing.LastModified > snapTs ? existing.LastModified : snapTs;
                merged[s.DocumentId] = existing with { Revision = rev, LastModified = ts };
            }
            else
            {
                merged[s.DocumentId] = new DocumentInfo(s.DocumentId, s.Revision, snapTs, 0);
            }
        }

        IEnumerable<DocumentInfo> projected = merged.Values.OrderBy(x => x.DocumentId, StringComparer.Ordinal);
        if (query.Skip is int skip && skip > 0) projected = projected.Skip(skip);
        if (query.Take is int take && take > 0) projected = projected.Take(take);

        foreach (var info in projected)
        {
            ct.ThrowIfCancellationRequested();
            yield return info;
        }
    }

    public async Task<DocumentInfo?> GetInfoAsync(string documentId, CancellationToken ct = default)
    {
        var snapshot = await _snapshots
            .Find(Builders<MongoDocumentSnapshot>.Filter.Eq(x => x.DocumentId, documentId))
            .Project(s => new { s.Revision, s.Timestamp })
            .FirstOrDefaultAsync(ct);

        var opsAgg = await _ops.Aggregate()
            .Match(Builders<MongoDocumentOp>.Filter.Eq(x => x.DocumentId, documentId))
            .Group(x => x.DocumentId, g => new
            {
                MaxRevision = g.Max(x => x.Revision),
                MaxTimestamp = g.Max(x => x.Timestamp),
                OpCount = g.LongCount()
            })
            .FirstOrDefaultAsync(ct);

        if (snapshot is null && opsAgg is null) return null;

        long revision = Math.Max(snapshot?.Revision ?? 0, opsAgg?.MaxRevision ?? 0);
        DateTimeOffset lastModified = DateTimeOffset.MinValue;
        if (snapshot is not null)
            lastModified = new DateTimeOffset(DateTime.SpecifyKind(snapshot.Timestamp, DateTimeKind.Utc));
        if (opsAgg is not null)
        {
            var opTs = new DateTimeOffset(DateTime.SpecifyKind(opsAgg.MaxTimestamp, DateTimeKind.Utc));
            if (opTs > lastModified) lastModified = opTs;
        }

        return new DocumentInfo(documentId, revision, lastModified, opsAgg?.OpCount ?? 0);
    }

    async Task IDocumentStore.DeleteAsync(string documentId, CancellationToken ct)
    {
        await _ops.DeleteManyAsync(Builders<MongoDocumentOp>.Filter.Eq(x => x.DocumentId, documentId), ct);
        await _snapshots.DeleteOneAsync(Builders<MongoDocumentSnapshot>.Filter.Eq(x => x.DocumentId, documentId), ct);
    }

    public async Task<int> DeleteByTenantPrefixAsync(string tenantPrefix, CancellationToken ct = default)
    {
        var regex = PrefixRegex(tenantPrefix);

        // Count distinct documents before deletion.
        var opDocs = await _ops.DistinctAsync(x => x.DocumentId,
            Builders<MongoDocumentOp>.Filter.Regex(x => x.DocumentId, regex), cancellationToken: ct);
        var snapDocs = await _snapshots.DistinctAsync(x => x.DocumentId,
            Builders<MongoDocumentSnapshot>.Filter.Regex(x => x.DocumentId, regex), cancellationToken: ct);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        await opDocs.ForEachAsync(id => ids.Add(id), ct);
        await snapDocs.ForEachAsync(id => ids.Add(id), ct);

        await _ops.DeleteManyAsync(Builders<MongoDocumentOp>.Filter.Regex(x => x.DocumentId, regex), ct);
        await _snapshots.DeleteManyAsync(Builders<MongoDocumentSnapshot>.Filter.Regex(x => x.DocumentId, regex), ct);

        return ids.Count;
    }

    async Task IHistoryStore.DeleteAsync(string documentId, CancellationToken ct)
    {
        await _historyOps.DeleteManyAsync(Builders<MongoHistoryOp>.Filter.Eq(x => x.DocumentId, documentId), ct);
        await _historySnapshots.DeleteManyAsync(Builders<MongoHistorySnapshot>.Filter.Eq(x => x.DocumentId, documentId), ct);
    }

    public async Task PurgeUpToAsync(string documentId, long upToRevision, CancellationToken ct = default)
    {
        await _historyOps.DeleteManyAsync(
            Builders<MongoHistoryOp>.Filter.Eq(x => x.DocumentId, documentId) &
            Builders<MongoHistoryOp>.Filter.Lte(x => x.Revision, upToRevision), ct);
        await _historySnapshots.DeleteManyAsync(
            Builders<MongoHistorySnapshot>.Filter.Eq(x => x.DocumentId, documentId) &
            Builders<MongoHistorySnapshot>.Filter.Lte(x => x.Revision, upToRevision), ct);
    }

    async Task<int> IHistoryStore.DeleteByTenantPrefixAsync(string tenantPrefix, CancellationToken ct)
    {
        var regex = PrefixRegex(tenantPrefix);

        var opDocs = await _historyOps.DistinctAsync(x => x.DocumentId,
            Builders<MongoHistoryOp>.Filter.Regex(x => x.DocumentId, regex), cancellationToken: ct);
        var snapDocs = await _historySnapshots.DistinctAsync(x => x.DocumentId,
            Builders<MongoHistorySnapshot>.Filter.Regex(x => x.DocumentId, regex), cancellationToken: ct);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        await opDocs.ForEachAsync(id => ids.Add(id), ct);
        await snapDocs.ForEachAsync(id => ids.Add(id), ct);

        await _historyOps.DeleteManyAsync(Builders<MongoHistoryOp>.Filter.Regex(x => x.DocumentId, regex), ct);
        await _historySnapshots.DeleteManyAsync(Builders<MongoHistorySnapshot>.Filter.Regex(x => x.DocumentId, regex), ct);

        return ids.Count;
    }
}
