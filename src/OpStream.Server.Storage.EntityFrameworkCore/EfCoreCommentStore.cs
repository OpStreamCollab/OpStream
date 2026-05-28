using Microsoft.EntityFrameworkCore;
using OpStream.Server.Comments;
using System.Text.Json;

namespace OpStream.Server.Storage.EntityFrameworkCore;

/// <summary>
/// EF Core implementation of <see cref="ICommentStore"/>. Works over any EF Core provider
/// (SQL Server, PostgreSQL, SQLite, MySQL…) via <see cref="OpStreamDbContext"/>.
/// </summary>
public class EfCoreCommentStore<TContext> : ICommentStore where TContext : OpStreamDbContext
{
    private readonly IDbContextFactory<TContext> _contextFactory;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public EfCoreCommentStore(IDbContextFactory<TContext> contextFactory)
        => _contextFactory = contextFactory;

    public async Task<IReadOnlyList<Comment>> LoadOpenAsync(string documentId, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        var entities = await db.Comments
            .AsNoTracking()
            .Where(c => c.DocumentId == documentId)
            .ToListAsync(ct);
        return entities.Select(ToModel).ToList();
    }

    public async Task<Comment?> GetAsync(string commentId, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        var entity = await db.Comments.FindAsync(new object[] { commentId }, ct);
        return entity is null ? null : ToModel(entity);
    }

    public async Task AddAsync(Comment comment, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        db.Comments.Add(ToEntity(comment));
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Comment comment, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        var entity = await db.Comments.FindAsync(new object[] { comment.Id }, ct);
        if (entity is null) return;
        ApplyToEntity(entity, comment);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string commentId, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        var entity = await db.Comments.FindAsync(new object[] { commentId }, ct);
        if (entity is null) return;

        db.Comments.Remove(entity);

        // Cascade replies when deleting a root comment.
        if (entity.ParentCommentId is null)
        {
            var replies = await db.Comments
                .Where(c => c.ParentCommentId == commentId)
                .ToListAsync(ct);
            db.Comments.RemoveRange(replies);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAnchorsAsync(string documentId, long revision,
        IReadOnlyList<AnchorUpdate> updates, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        var ids = updates.Select(u => u.CommentId).ToList();
        var entities = await db.Comments
            .Where(c => ids.Contains(c.Id))
            .ToListAsync(ct);

        var map = entities.ToDictionary(e => e.Id, StringComparer.Ordinal);
        foreach (var u in updates)
        {
            if (!map.TryGetValue(u.CommentId, out var entity)) continue;
            entity.AnchorJson = JsonSerializer.Serialize(u.Anchor, JsonOptions);
            entity.AnchoredAtRevision = revision;
            entity.IsOrphaned = u.Outcome == AnchorOutcome.Orphaned || entity.IsOrphaned;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<long?> GetMinAnchoredRevisionAsync(string documentId, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        var min = await db.Comments
            .AsNoTracking()
            .Where(c => c.DocumentId == documentId
                        && c.ParentCommentId == null
                        && c.ResolvedAt == null
                        && c.AnchorJson != null)
            .Select(c => (long?)c.AnchoredAtRevision)
            .MinAsync(ct);
        return min;
    }

    // ─── Mapping ─────────────────────────────────────────────────────────────

    private static Comment ToModel(CommentEntity e)
    {
        Anchor? anchor = null;
        if (e.AnchorJson is not null)
        {
            try { anchor = JsonSerializer.Deserialize<Anchor>(e.AnchorJson, JsonOptions); }
            catch { /* malformed JSON — treat as no anchor */ }
        }

        return new Comment(
            e.Id, e.DocumentId, e.ParentCommentId, e.AuthorPeerId, e.Body,
            anchor, e.AnchoredAtRevision, e.CreatedAt, e.ResolvedAt,
            e.ResolvedByPeerId, e.IsOrphaned);
    }

    private static CommentEntity ToEntity(Comment c) => new()
    {
        Id = c.Id,
        DocumentId = c.DocumentId,
        ParentCommentId = c.ParentCommentId,
        AuthorPeerId = c.AuthorPeerId,
        Body = c.Body,
        AnchorJson = c.Anchor is null ? null : JsonSerializer.Serialize(c.Anchor, JsonOptions),
        AnchoredAtRevision = c.AnchoredAtRevision,
        CreatedAt = c.CreatedAt,
        ResolvedAt = c.ResolvedAt,
        ResolvedByPeerId = c.ResolvedByPeerId,
        IsOrphaned = c.IsOrphaned
    };

    private static void ApplyToEntity(CommentEntity entity, Comment c)
    {
        entity.Body = c.Body;
        entity.AnchorJson = c.Anchor is null ? null : JsonSerializer.Serialize(c.Anchor, JsonOptions);
        entity.AnchoredAtRevision = c.AnchoredAtRevision;
        entity.ResolvedAt = c.ResolvedAt;
        entity.ResolvedByPeerId = c.ResolvedByPeerId;
        entity.IsOrphaned = c.IsOrphaned;
    }
}
