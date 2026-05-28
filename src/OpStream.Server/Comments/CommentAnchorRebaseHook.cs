using Microsoft.Extensions.Logging;
using OpStream.Server.Session;

namespace OpStream.Server.Comments;

/// <summary>
/// <see cref="IPostApplyHook{TOp}"/> that rebases every open root-comment anchor against the
/// op that was just applied. Registered as an open generic — kicks in only for document types
/// that have a matching <see cref="IAnchorEngine{TOp}"/> registered; otherwise it is a no-op.
/// </summary>
public class CommentAnchorRebaseHook<TOp> : IPostApplyHook<TOp>
{
    private readonly IAnchorEngine<TOp>? _anchorEngine;
    private readonly ICommentStore _commentStore;
    private readonly ILogger<CommentAnchorRebaseHook<TOp>> _logger;

    public CommentAnchorRebaseHook(
        IEnumerable<IAnchorEngine<TOp>> anchorEngines,
        ICommentStore commentStore,
        ILogger<CommentAnchorRebaseHook<TOp>> logger)
    {
        _anchorEngine = anchorEngines.FirstOrDefault();
        _commentStore = commentStore;
        _logger = logger;
    }

    public async ValueTask<PostApplyResult> AfterApplyAsync(PostApplyContext<TOp> ctx, CancellationToken ct)
    {
        if (_anchorEngine is null) return PostApplyResult.Empty;

        IReadOnlyList<Comment> comments;
        try
        {
            comments = await _commentStore.LoadOpenAsync(ctx.DocumentId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load comments for rebase on {DocId}", ctx.DocumentId);
            return PostApplyResult.Empty;
        }

        if (comments.Count == 0) return PostApplyResult.Empty;

        List<AnchorUpdate>? updates = null;
        foreach (var comment in comments)
        {
            // Replies don't have anchors.
            if (comment.ParentCommentId is not null) continue;
            if (comment.Anchor is null) continue;
            // Already orphaned anchors don't move further.
            if (comment.IsOrphaned) continue;
            // Anchors at or above this revision have already been accounted for.
            if (comment.AnchoredAtRevision >= ctx.Revision) continue;

            AnchorRebaseResult rebase;
            try
            {
                rebase = _anchorEngine.Rebase(comment.Anchor, ctx.AppliedOp);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Anchor rebase threw for comment {CommentId} on {DocId}", comment.Id, ctx.DocumentId);
                continue;
            }

            if (rebase.Outcome == AnchorOutcome.Unchanged && !comment.IsOrphaned)
                continue;

            updates ??= new List<AnchorUpdate>();
            updates.Add(new AnchorUpdate(comment.Id, rebase.Anchor, rebase.Outcome));
        }

        if (updates is null) return PostApplyResult.Empty;

        try
        {
            await _commentStore.UpdateAnchorsAsync(ctx.DocumentId, ctx.Revision, updates, ct);
        }
        catch (Exception ex)
        {
            // Persistence of anchor updates is best-effort. The op is already in the log; on
            // rehydration we will replay this hook and try again.
            _logger.LogError(ex, "Failed to persist anchor updates for {DocId} rev {Revision}",
                ctx.DocumentId, ctx.Revision);
        }

        return new PostApplyResult(updates);
    }
}
