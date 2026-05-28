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
    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _db;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private const string SnapshotSuffix = ":snapshot";
    private const string OpsSuffix = ":ops";
    private const string HistoryOpsSuffix = ":history_ops";
    private const string HistorySnapshotsSuffix = ":history_snapshots";

    public RedisDocumentStore(IConnectionMultiplexer redis)
    {
        _redis = redis;
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

    // ─── Management surface ──────────────────────────────────────────────────

    private static string EscapeGlob(string raw)
    {
        // SCAN patterns are glob-style. Escape *, ?, [, ], \ so a tenant id never
        // accidentally widens the match.
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (c is '*' or '?' or '[' or ']' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private async IAsyncEnumerable<string> ScanKeysAsync(string pattern, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var endpoint in _redis.GetEndPoints())
        {
            var server = _redis.GetServer(endpoint);
            if (server.IsReplica || !server.IsConnected) continue;

            await foreach (var key in server.KeysAsync(pattern: pattern, pageSize: 250).WithCancellation(ct))
            {
                yield return key.ToString();
            }
        }
    }

    private static string? TryExtractDocumentId(string key, string suffix)
    {
        const string KeyPrefix = "doc:";
        if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal)) return null;
        if (!key.EndsWith(suffix, StringComparison.Ordinal)) return null;
        return key.Substring(KeyPrefix.Length, key.Length - KeyPrefix.Length - suffix.Length);
    }

    public async IAsyncEnumerable<DocumentInfo> EnumerateAsync(
        string tenantPrefix,
        DocumentQuery query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var globPrefix = EscapeGlob(tenantPrefix);

        // A document may exist with only a snapshot, only ops, or both.
        var docIds = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var key in ScanKeysAsync($"doc:{globPrefix}*{SnapshotSuffix}", ct))
            if (TryExtractDocumentId(key, SnapshotSuffix) is { } id) docIds.Add(id);
        await foreach (var key in ScanKeysAsync($"doc:{globPrefix}*{OpsSuffix}", ct))
            if (TryExtractDocumentId(key, OpsSuffix) is { } id) docIds.Add(id);

        IEnumerable<string> projected = docIds.OrderBy(x => x, StringComparer.Ordinal);
        if (query.Skip is int skip && skip > 0) projected = projected.Skip(skip);
        if (query.Take is int take && take > 0) projected = projected.Take(take);

        foreach (var id in projected)
        {
            ct.ThrowIfCancellationRequested();
            var info = await GetInfoAsync(id, ct);
            if (info is not null) yield return info;
        }
    }

    public async Task<DocumentInfo?> GetInfoAsync(string documentId, CancellationToken ct = default)
    {
        var snapshotKey = GetSnapshotKey(documentId);
        var opsKey = GetOpsKey(documentId);

        var snapshotTask = _db.StringGetAsync(snapshotKey);
        var opCountTask = _db.StreamLengthAsync(opsKey);
        var lastOpTask = _db.StreamRangeAsync(opsKey, "+", "-", count: 1, messageOrder: Order.Descending);

        await Task.WhenAll(snapshotTask, opCountTask, lastOpTask);

        var snapshotData = snapshotTask.Result;
        var opCount = opCountTask.Result;
        var lastOp = lastOpTask.Result;

        DocumentSnapshot? snapshot = snapshotData.IsNull
            ? null
            : JsonSerializer.Deserialize<DocumentSnapshot>(snapshotData.ToString(), JsonOptions);

        long? opMaxRev = null;
        DateTimeOffset? opMaxTs = null;
        if (lastOp.Length > 0)
        {
            var op = DeserializeOp(lastOp[0]);
            opMaxRev = op.Revision;
            opMaxTs = op.Timestamp;
        }

        if (snapshot is null && opMaxRev is null) return null;

        long revision = Math.Max(snapshot?.Revision ?? 0, opMaxRev ?? 0);
        DateTimeOffset lastModified = DateTimeOffset.MinValue;
        if (snapshot is not null && snapshot.Timestamp > lastModified) lastModified = snapshot.Timestamp;
        if (opMaxTs is DateTimeOffset ts && ts > lastModified) lastModified = ts;

        return new DocumentInfo(documentId, revision, lastModified, opCount);
    }

    async Task IDocumentStore.DeleteAsync(string documentId, CancellationToken ct)
    {
        await _db.KeyDeleteAsync(new RedisKey[]
        {
            GetSnapshotKey(documentId),
            GetOpsKey(documentId)
        });
    }

    public async Task<int> DeleteByTenantPrefixAsync(string tenantPrefix, CancellationToken ct = default)
    {
        var globPrefix = EscapeGlob(tenantPrefix);

        var docIds = new HashSet<string>(StringComparer.Ordinal);
        var keysToDelete = new List<RedisKey>();

        await foreach (var key in ScanKeysAsync($"doc:{globPrefix}*{SnapshotSuffix}", ct))
        {
            if (TryExtractDocumentId(key, SnapshotSuffix) is { } id) docIds.Add(id);
            keysToDelete.Add(key);
        }
        await foreach (var key in ScanKeysAsync($"doc:{globPrefix}*{OpsSuffix}", ct))
        {
            if (TryExtractDocumentId(key, OpsSuffix) is { } id) docIds.Add(id);
            keysToDelete.Add(key);
        }

        await DeleteInBatchesAsync(keysToDelete);
        return docIds.Count;
    }

    async Task IHistoryStore.DeleteAsync(string documentId, CancellationToken ct)
    {
        await _db.KeyDeleteAsync(new RedisKey[]
        {
            GetHistoryOpsKey(documentId),
            GetHistorySnapshotsKey(documentId)
        });
    }

    public async Task PurgeUpToAsync(string documentId, long upToRevision, CancellationToken ct = default)
    {
        var opsKey = GetHistoryOpsKey(documentId);
        var snapshotsKey = GetHistorySnapshotsKey(documentId);

        // Trim the immutable history stream up to (and including) upToRevision.
        await _db.ExecuteAsync("XTRIM", opsKey, "MINID", $"{upToRevision + 1}-0");

        // Sorted-set scores match revision numbers — remove everything <= upToRevision.
        await _db.SortedSetRemoveRangeByScoreAsync(snapshotsKey, double.NegativeInfinity, upToRevision);
    }

    async Task<int> IHistoryStore.DeleteByTenantPrefixAsync(string tenantPrefix, CancellationToken ct)
    {
        var globPrefix = EscapeGlob(tenantPrefix);

        var docIds = new HashSet<string>(StringComparer.Ordinal);
        var keysToDelete = new List<RedisKey>();

        await foreach (var key in ScanKeysAsync($"doc:{globPrefix}*{HistoryOpsSuffix}", ct))
        {
            if (TryExtractDocumentId(key, HistoryOpsSuffix) is { } id) docIds.Add(id);
            keysToDelete.Add(key);
        }
        await foreach (var key in ScanKeysAsync($"doc:{globPrefix}*{HistorySnapshotsSuffix}", ct))
        {
            if (TryExtractDocumentId(key, HistorySnapshotsSuffix) is { } id) docIds.Add(id);
            keysToDelete.Add(key);
        }

        await DeleteInBatchesAsync(keysToDelete);
        return docIds.Count;
    }

    private async Task DeleteInBatchesAsync(List<RedisKey> keys)
    {
        const int batchSize = 500;
        for (var i = 0; i < keys.Count; i += batchSize)
        {
            var slice = keys.GetRange(i, Math.Min(batchSize, keys.Count - i));
            await _db.KeyDeleteAsync(slice.ToArray());
        }
    }
}
