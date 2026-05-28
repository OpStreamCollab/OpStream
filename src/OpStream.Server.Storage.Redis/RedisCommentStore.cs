using StackExchange.Redis;
using OpStream.Server.Comments;
using System.Text.Json;

namespace OpStream.Server.Storage.Redis;

/// <summary>
/// Redis implementation of <see cref="ICommentStore"/>.
/// Uses a Hash <c>comments:{documentId}</c> keyed by <c>commentId</c>,
/// with each value being a JSON-encoded <see cref="Comment"/>.
/// A separate sorted set <c>comments:{documentId}:byrev</c> is maintained for
/// <see cref="GetMinAnchoredRevisionAsync"/> without a full scan.
/// </summary>
public class RedisCommentStore : ICommentStore
{
    private readonly IDatabase _db;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public RedisCommentStore(IConnectionMultiplexer redis)
        => _db = redis.GetDatabase();

    // ─── Key helpers ─────────────────────────────────────────────────────────

    private static string HashKey(string documentId) => $"comments:{documentId}";
    private static string RevKey(string documentId)  => $"comments:{documentId}:byrev";

    // ─── ICommentStore ────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Comment>> LoadOpenAsync(string documentId, CancellationToken ct = default)
    {
        var entries = await _db.HashGetAllAsync(HashKey(documentId));
        var result = new List<Comment>(entries.Length);
        foreach (var entry in entries)
        {
            var comment = Deserialize(entry.Value);
            if (comment is not null) result.Add(comment);
        }
        return result;
    }

    public async Task<Comment?> GetAsync(string commentId, CancellationToken ct = default)
    {
        // We must search across documents — do a key scan limited to the commentId pattern.
        // In practice callers always know the documentId; if needed in the future we can
        // add a reverse-index. For now we store a lookup key: comments:id:{commentId} → docId.
        var docId = await _db.StringGetAsync(IdIndexKey(commentId));
        if (docId.IsNull) return null;

        var raw = await _db.HashGetAsync(HashKey(docId!), commentId);
        return raw.IsNull ? null : Deserialize(raw);
    }

    public async Task AddAsync(Comment comment, CancellationToken ct = default)
    {
        var json = Serialize(comment);
        var hashKey = HashKey(comment.DocumentId);
        var revKey = RevKey(comment.DocumentId);

        var batch = _db.CreateBatch();
        var tasks = new List<Task>
        {
            batch.HashSetAsync(hashKey, comment.Id, json),
            batch.StringSetAsync(IdIndexKey(comment.Id), comment.DocumentId),
        };

        // Track anchored revision only for root comments with an anchor.
        if (comment.ParentCommentId is null && comment.Anchor is not null && comment.ResolvedAt is null)
            tasks.Add(batch.SortedSetAddAsync(revKey, comment.Id, comment.AnchoredAtRevision));

        batch.Execute();
        await Task.WhenAll(tasks);
    }

    public async Task UpdateAsync(Comment comment, CancellationToken ct = default)
    {
        var hashKey = HashKey(comment.DocumentId);
        var revKey = RevKey(comment.DocumentId);
        var json = Serialize(comment);

        var batch = _db.CreateBatch();
        var tasks = new List<Task> { batch.HashSetAsync(hashKey, comment.Id, json) };

        if (comment.ParentCommentId is null && comment.Anchor is not null)
        {
            if (comment.ResolvedAt is not null)
                tasks.Add(batch.SortedSetRemoveAsync(revKey, comment.Id));
            else
                tasks.Add(batch.SortedSetAddAsync(revKey, comment.Id, comment.AnchoredAtRevision));
        }

        batch.Execute();
        await Task.WhenAll(tasks);
    }

    public async Task DeleteAsync(string commentId, CancellationToken ct = default)
    {
        var docId = (string?)await _db.StringGetAsync(IdIndexKey(commentId));
        if (docId is null) return;

        var comment = Deserialize(await _db.HashGetAsync(HashKey(docId), commentId));
        if (comment is null) return;

        // Collect ids to remove: the root + its replies.
        var toRemove = new List<string> { commentId };
        if (comment.ParentCommentId is null)
        {
            var all = await _db.HashGetAllAsync(HashKey(docId));
            foreach (var entry in all)
            {
                var c = Deserialize(entry.Value);
                if (c?.ParentCommentId == commentId) toRemove.Add(c.Id);
            }
        }

        var batch = _db.CreateBatch();
        var tasks = new List<Task>();
        foreach (var id in toRemove)
        {
            tasks.Add(batch.HashDeleteAsync(HashKey(docId), id));
            tasks.Add(batch.KeyDeleteAsync(IdIndexKey(id)));
            tasks.Add(batch.SortedSetRemoveAsync(RevKey(docId), id));
        }
        batch.Execute();
        await Task.WhenAll(tasks);
    }

    public async Task UpdateAnchorsAsync(string documentId, long revision,
        IReadOnlyList<AnchorUpdate> updates, CancellationToken ct = default)
    {
        // Use a Lua script to keep the update atomic per-document.
        const string lua = @"
local hashKey = KEYS[1]
local revKey  = KEYS[2]
local revision = tonumber(ARGV[1])
for i = 2, #ARGV, 3 do
    local commentId = ARGV[i]
    local anchorJson = ARGV[i+1]
    local orphaned   = ARGV[i+2]
    local raw = redis.call('HGET', hashKey, commentId)
    if raw then
        local obj = cjson.decode(raw)
        obj['anchor'] = cjson.decode(anchorJson)
        obj['anchoredAtRevision'] = revision
        if orphaned == '1' then obj['isOrphaned'] = true end
        redis.call('HSET', hashKey, commentId, cjson.encode(obj))
        redis.call('ZADD', revKey, revision, commentId)
    end
end
return 1";

        var keys = new RedisKey[] { HashKey(documentId), RevKey(documentId) };
        var args = new List<RedisValue> { revision.ToString() };
        foreach (var u in updates)
        {
            args.Add(u.CommentId);
            args.Add(JsonSerializer.Serialize(u.Anchor, JsonOptions));
            args.Add(u.Outcome == AnchorOutcome.Orphaned ? "1" : "0");
        }

        await _db.ScriptEvaluateAsync(lua, keys, args.ToArray());
    }

    public async Task<long?> GetMinAnchoredRevisionAsync(string documentId, CancellationToken ct = default)
    {
        var entries = await _db.SortedSetRangeByRankWithScoresAsync(RevKey(documentId), 0, 0);
        if (entries.Length == 0) return null;
        return (long)entries[0].Score;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static string IdIndexKey(string commentId) => $"comments:id:{commentId}";

    private static string Serialize(Comment c) =>
        JsonSerializer.Serialize(c, JsonOptions);

    private static Comment? Deserialize(RedisValue value)
    {
        if (value.IsNull || value.IsNullOrEmpty) return null;
        try { return JsonSerializer.Deserialize<Comment>(value.ToString(), JsonOptions); }
        catch { return null; }
    }
}
