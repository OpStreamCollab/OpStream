using MongoDB.Driver;
using OpStream.Server.Comments;
using System.Text.Json;

namespace OpStream.Server.Storage.MongoDB;

/// <summary>
/// MongoDB implementation of <see cref="ICommentStore"/>.
/// Uses a single collection <c>comments</c> keyed by <c>commentId</c>.
/// <para>
/// <b>Atomicity caveat:</b> without a replica set, MongoDB has no multi-document transactions.
/// The documented recovery contract is replay via <c>RehydrateOpAsync</c> →
/// <c>CommentAnchorRebaseHook</c>, which is already wired up.
/// </para>
/// </summary>
public class MongoCommentStore : ICommentStore
{
    private readonly IMongoCollection<MongoComment> _comments;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public MongoCommentStore(IMongoDatabase database)
    {
        _comments = database.GetCollection<MongoComment>("comments");

        _comments.Indexes.CreateMany(new[]
        {
            new CreateIndexModel<MongoComment>(
                Builders<MongoComment>.IndexKeys
                    .Ascending(x => x.DocumentId)
                    .Ascending(x => x.ParentCommentId)
                    .Ascending(x => x.ResolvedAt)),
        });
    }

    public async Task<IReadOnlyList<Comment>> LoadOpenAsync(string documentId, CancellationToken ct = default)
    {
        var filter = Builders<MongoComment>.Filter.Eq(x => x.DocumentId, documentId);
        var entities = await _comments.Find(filter).ToListAsync(ct);
        return entities.Select(ToModel).ToList();
    }

    public async Task<Comment?> GetAsync(string commentId, CancellationToken ct = default)
    {
        var filter = Builders<MongoComment>.Filter.Eq(x => x.Id, commentId);
        var entity = await _comments.Find(filter).FirstOrDefaultAsync(ct);
        return entity is null ? null : ToModel(entity);
    }

    public async Task AddAsync(Comment comment, CancellationToken ct = default)
    {
        await _comments.InsertOneAsync(ToEntity(comment), cancellationToken: ct);
    }

    public async Task UpdateAsync(Comment comment, CancellationToken ct = default)
    {
        var filter = Builders<MongoComment>.Filter.Eq(x => x.Id, comment.Id);
        var update = Builders<MongoComment>.Update
            .Set(x => x.Body, comment.Body)
            .Set(x => x.AnchorJson, comment.Anchor is null ? null : JsonSerializer.Serialize(comment.Anchor, JsonOptions))
            .Set(x => x.AnchoredAtRevision, comment.AnchoredAtRevision)
            .Set(x => x.ResolvedAt, comment.ResolvedAt)
            .Set(x => x.ResolvedByPeerId, comment.ResolvedByPeerId)
            .Set(x => x.IsOrphaned, comment.IsOrphaned);
        await _comments.UpdateOneAsync(filter, update, cancellationToken: ct);
    }

    public async Task DeleteAsync(string commentId, CancellationToken ct = default)
    {
        var entity = await GetAsync(commentId, ct);
        if (entity is null) return;

        var filter = Builders<MongoComment>.Filter.Eq(x => x.Id, commentId);
        await _comments.DeleteOneAsync(filter, ct);

        // Cascade: remove replies of a root comment.
        if (entity.ParentCommentId is null)
        {
            var repliesFilter = Builders<MongoComment>.Filter.Eq(x => x.ParentCommentId, commentId);
            await _comments.DeleteManyAsync(repliesFilter, ct);
        }
    }

    public async Task UpdateAnchorsAsync(string documentId, long revision,
        IReadOnlyList<AnchorUpdate> updates, CancellationToken ct = default)
    {
        foreach (var u in updates)
        {
            var filter = Builders<MongoComment>.Filter.Eq(x => x.Id, u.CommentId);
            var existing = await _comments.Find(filter).FirstOrDefaultAsync(ct);
            if (existing is null) continue;

            var update = Builders<MongoComment>.Update
                .Set(x => x.AnchorJson, JsonSerializer.Serialize(u.Anchor, JsonOptions))
                .Set(x => x.AnchoredAtRevision, revision)
                .Set(x => x.IsOrphaned, u.Outcome == AnchorOutcome.Orphaned || existing.IsOrphaned);
            await _comments.UpdateOneAsync(filter, update, cancellationToken: ct);
        }
    }

    public async Task<long?> GetMinAnchoredRevisionAsync(string documentId, CancellationToken ct = default)
    {
        var filter = Builders<MongoComment>.Filter.Eq(x => x.DocumentId, documentId)
                   & Builders<MongoComment>.Filter.Eq(x => x.ParentCommentId, null)
                   & Builders<MongoComment>.Filter.Eq(x => x.ResolvedAt, null)
                   & Builders<MongoComment>.Filter.Ne(x => x.AnchorJson, null);

        var sort = Builders<MongoComment>.Sort.Ascending(x => x.AnchoredAtRevision);
        var first = await _comments.Find(filter).Sort(sort).FirstOrDefaultAsync(ct);
        return first is null ? null : first.AnchoredAtRevision;
    }

    // ─── Mapping ─────────────────────────────────────────────────────────────

    private static Comment ToModel(MongoComment e)
    {
        Anchor? anchor = null;
        if (e.AnchorJson is not null)
        {
            try { anchor = JsonSerializer.Deserialize<Anchor>(e.AnchorJson, JsonOptions); }
            catch { /* malformed — skip */ }
        }
        return new Comment(e.Id, e.DocumentId, e.ParentCommentId, e.AuthorPeerId, e.AuthorName, e.Body,
            anchor, e.AnchoredAtRevision, e.CreatedAt, e.ResolvedAt, e.ResolvedByPeerId, e.IsOrphaned);
    }

    private static MongoComment ToEntity(Comment c) => new()
    {
        Id = c.Id,
        DocumentId = c.DocumentId,
        ParentCommentId = c.ParentCommentId,
        AuthorPeerId = c.AuthorPeerId,
        AuthorName = c.AuthorName,
        Body = c.Body,
        AnchorJson = c.Anchor is null ? null : JsonSerializer.Serialize(c.Anchor, JsonOptions),
        AnchoredAtRevision = c.AnchoredAtRevision,
        CreatedAt = c.CreatedAt,
        ResolvedAt = c.ResolvedAt,
        ResolvedByPeerId = c.ResolvedByPeerId,
        IsOrphaned = c.IsOrphaned
    };
}
