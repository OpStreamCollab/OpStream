using System.Collections.Concurrent;

namespace OpStream.Server.Comments;

/// <summary>
/// In-memory <see cref="ICommentStore"/>. Default registration in <c>AddOpStream()</c>.
/// Not for production: comments are lost when the process restarts even when ops are persisted.
/// </summary>
public class MemoryCommentStore : ICommentStore
{
    // commentId → Comment
    private readonly ConcurrentDictionary<string, Comment> _comments = new(StringComparer.Ordinal);
    // documentId → set of commentIds (for O(1) per-doc enumeration)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _byDocument =
        new(StringComparer.Ordinal);

    public Task<IReadOnlyList<Comment>> LoadOpenAsync(string documentId, CancellationToken ct = default)
    {
        if (!_byDocument.TryGetValue(documentId, out var ids))
            return Task.FromResult<IReadOnlyList<Comment>>(Array.Empty<Comment>());

        var list = new List<Comment>(ids.Count);
        foreach (var id in ids.Keys)
            if (_comments.TryGetValue(id, out var c)) list.Add(c);
        return Task.FromResult<IReadOnlyList<Comment>>(list);
    }

    public Task<Comment?> GetAsync(string commentId, CancellationToken ct = default)
    {
        _comments.TryGetValue(commentId, out var c);
        return Task.FromResult(c);
    }

    public Task AddAsync(Comment comment, CancellationToken ct = default)
    {
        _comments[comment.Id] = comment;
        var set = _byDocument.GetOrAdd(comment.DocumentId, _ => new(StringComparer.Ordinal));
        set.TryAdd(comment.Id, 0);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Comment comment, CancellationToken ct = default)
    {
        _comments[comment.Id] = comment;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string commentId, CancellationToken ct = default)
    {
        if (_comments.TryRemove(commentId, out var c))
        {
            if (_byDocument.TryGetValue(c.DocumentId, out var set))
                set.TryRemove(commentId, out _);

            // Cascade: replies of a root vanish with the root.
            if (c.ParentCommentId is null)
            {
                foreach (var reply in _comments.Values.Where(x => x.ParentCommentId == commentId).ToArray())
                {
                    _comments.TryRemove(reply.Id, out _);
                    if (_byDocument.TryGetValue(reply.DocumentId, out var rset))
                        rset.TryRemove(reply.Id, out _);
                }
            }
        }
        return Task.CompletedTask;
    }

    public Task UpdateAnchorsAsync(string documentId, long revision,
        IReadOnlyList<AnchorUpdate> updates, CancellationToken ct = default)
    {
        foreach (var u in updates)
        {
            if (!_comments.TryGetValue(u.CommentId, out var existing)) continue;
            _comments[u.CommentId] = existing with
            {
                Anchor = u.Anchor,
                AnchoredAtRevision = revision,
                IsOrphaned = u.Outcome == AnchorOutcome.Orphaned || existing.IsOrphaned
            };
        }
        return Task.CompletedTask;
    }

    public Task<long?> GetMinAnchoredRevisionAsync(string documentId, CancellationToken ct = default)
    {
        if (!_byDocument.TryGetValue(documentId, out var ids))
            return Task.FromResult<long?>(null);

        long? min = null;
        foreach (var id in ids.Keys)
        {
            if (!_comments.TryGetValue(id, out var c)) continue;
            if (c.ParentCommentId is not null) continue;          // only root comments have anchors
            if (c.ResolvedAt is not null) continue;
            if (c.Anchor is null) continue;
            if (min is null || c.AnchoredAtRevision < min) min = c.AnchoredAtRevision;
        }
        return Task.FromResult(min);
    }
}
