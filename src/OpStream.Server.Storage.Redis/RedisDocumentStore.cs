using StackExchange.Redis;
using OpStream.Server.Models;
using OpStream.Server.Storage;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace OpStream.Server.Storage.Redis;

/// <summary>
/// Redis implementation of the document and history store.
/// Uses Streams for operations and Strings for snapshots.
/// </summary>
public class RedisDocumentStore : IDocumentStore, IHistoryStore
{
    private readonly IDatabase _db;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public RedisDocumentStore(IConnectionMultiplexer redis)
    {
        _db = redis.GetDatabase();
    }

    private string GetOpsKey(string documentId) => $"doc:{documentId}:ops";
    private string GetSnapshotKey(string documentId) => $"doc:{documentId}:snapshot";
    private string GetHistoryOpsKey(string documentId) => $"doc:{documentId}:history_ops";
    private string GetHistorySnapshotsKey(string documentId) => $"doc:{documentId}:history_snapshots";

    public async Task<DocumentSnapshot?> LoadSnapshotAsync(string documentId, CancellationToken ct = default)
    {
        RedisValue data = await _db.StringGetAsync(GetSnapshotKey(documentId));
        if (data.IsNull) return null;

        return JsonSerializer.Deserialize<DocumentSnapshot>(data.ToString(), JsonOptions);
    }

    public async IAsyncEnumerable<StoredOp> StreamOpsAsync(string documentId, long sinceRevision, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var key = GetOpsKey(documentId);
        var startId = $"{sinceRevision + 1}-0";
        var entries = await _db.StreamRangeAsync(key, startId, "+");

        foreach (var entry in entries)
        {
            yield return DeserializeOp(entry);
        }
    }

    public async Task AppendOpAsync(string documentId, StoredOp op, CancellationToken ct = default)
    {
        var key = GetOpsKey(documentId);
        await _db.StreamAddAsync(key, GetStreamValues(op), messageId: $"{op.Revision}-0");
    }

    public async Task WriteSnapshotAsync(string documentId, DocumentSnapshot snapshot, CancellationToken ct = default)
    {
        var key = GetSnapshotKey(documentId);
        var data = JsonSerializer.Serialize(snapshot, JsonOptions);
        await _db.StringSetAsync(key, data);
    }

    public async Task CompactAsync(string documentId, long upToRevision, CancellationToken ct = default)
    {
        var key = GetOpsKey(documentId);
        // StackExchange.Redis StreamTrimAsync overload:
        // StreamTrimAsync(RedisKey key, long count, bool approximate = false, CommandFlags flags = CommandFlags.None)
        // This is count-based. To use minId we need the enum. 
        // If the enum is not found, it might be because of versioning or missing using.
        // Let's try to use the long count overload as a temporary workaround if needed, 
        // but we really want minId.
        
        // I will try to use the raw call via ExecuteAsync if the typed one fails.
        await _db.ExecuteAsync("XTRIM", key, "MINID", $"{upToRevision + 1}-0");
    }

    public async Task<DocumentSnapshot?> LoadHistorySnapshotAsync(string documentId, long maxRevision, CancellationToken ct = default)
    {
        var key = GetHistorySnapshotsKey(documentId);
        var entries = await _db.SortedSetRangeByScoreAsync(key, stop: maxRevision, order: Order.Descending, take: 1);
        if (entries.Length == 0) return null;

        return JsonSerializer.Deserialize<DocumentSnapshot>(entries[0].ToString(), JsonOptions);
    }

    public async IAsyncEnumerable<StoredOp> StreamHistoryOpsAsync(string documentId, long sinceRevision, long upToRevision, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var key = GetHistoryOpsKey(documentId);
        var startId = $"{sinceRevision + 1}-0";
        var endId = $"{upToRevision}-0";
        
        var entries = await _db.StreamRangeAsync(key, startId, endId);

        foreach (var entry in entries)
        {
            yield return DeserializeOp(entry);
        }
    }

    public async Task<IEnumerable<HistoryMilestone>> GetMilestonesAsync(string documentId, CancellationToken ct = default)
    {
        var key = GetHistorySnapshotsKey(documentId);
        var entries = await _db.SortedSetRangeByScoreAsync(key, order: Order.Descending);
        
        return entries.Select(e => {
            var s = JsonSerializer.Deserialize<DocumentSnapshot>(e.ToString(), JsonOptions)!;
            return new HistoryMilestone(s.Revision, s.Timestamp, null);
        });
    }

    public async Task WriteHistorySnapshotAsync(string documentId, DocumentSnapshot snapshot, string? name = null, CancellationToken ct = default)
    {
        var key = GetHistorySnapshotsKey(documentId);
        var data = JsonSerializer.Serialize(snapshot, JsonOptions);
        await _db.SortedSetAddAsync(key, data, snapshot.Revision);
    }

    public async Task AppendHistoryOpAsync(string documentId, StoredOp op, CancellationToken ct = default)
    {
        var key = GetHistoryOpsKey(documentId);
        await _db.StreamAddAsync(key, GetStreamValues(op), messageId: $"{op.Revision}-0");
    }

    private NameValueEntry[] GetStreamValues(StoredOp op)
    {
        return new[]
        {
            new NameValueEntry("rev", op.Revision),
            new NameValueEntry("auth", op.AuthorId),
            new NameValueEntry("ts", op.Timestamp.ToUnixTimeMilliseconds()),
            new NameValueEntry("payload", op.Payload.ToArray()),
            new NameValueEntry("engine", op.EngineType)
        };
    }

    private StoredOp DeserializeOp(StreamEntry entry)
    {
        var values = entry.Values.ToDictionary(x => x.Name.ToString(), x => x.Value);
        return new StoredOp(
            (long)values["rev"],
            values["auth"].ToString(),
            DateTimeOffset.FromUnixTimeMilliseconds((long)values["ts"]),
            (byte[])values["payload"]!,
            values["engine"].ToString()
        );
    }
}
