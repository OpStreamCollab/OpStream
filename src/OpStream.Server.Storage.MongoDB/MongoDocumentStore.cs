using MongoDB.Driver;
using OpStream.Server.Models;
using OpStream.Server.Storage;
using System.Runtime.CompilerServices;

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
}
