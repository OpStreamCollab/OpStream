using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpStream.Server.Diagnostics;
using OpStream.Server.Storage;

namespace OpStream.Server.Comments;

/// <summary>
/// Runs a full anchor rebase pass before delegating to <see cref="IDocumentStore.CompactAsync"/>.
/// Without this, open comments whose <c>AnchoredAtRevision</c> falls below the compaction floor
/// can no longer recover their positions from the op log.
/// </summary>
public sealed class CompactWithAnchorsService(
    ICommentStore commentStore,
    IDocumentStore documentStore,
    IAnchorEngineRegistry anchorRegistry,
    ILogger<CompactWithAnchorsService> logger)
{
    /// <summary>
    /// Rebases all open comment anchors for <paramref name="documentId"/> against every op
    /// in <c>[minAnchoredRevision … upToRevision]</c>, then delegates to
    /// <see cref="IDocumentStore.CompactAsync"/>.
    /// </summary>
    /// <param name="documentId">Global document id (already resolved by the caller).</param>
    /// <param name="upToRevision">Inclusive upper bound passed to the store compaction call.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task CompactAsync(string documentId, long upToRevision, CancellationToken ct = default)
    {
        var minRevision = await commentStore.GetMinAnchoredRevisionAsync(documentId, ct);

        if (minRevision.HasValue && minRevision.Value <= upToRevision)
        {
            await RebaseAnchorsAsync(documentId, minRevision.Value, upToRevision, ct);
        }

        await documentStore.CompactAsync(documentId, upToRevision, ct);
    }

    private async Task RebaseAnchorsAsync(
        string documentId,
        long fromRevision,
        long upToRevision,
        CancellationToken ct)
    {
        using var activity = OpStreamTelemetry.ActivitySource.StartActivity("opstream.comments.compact_rebase");
        var sw = Stopwatch.GetTimestamp();
        var comments = await commentStore.LoadOpenAsync(documentId, ct);
        var openRoots = comments
            .Where(c => c.ParentCommentId is null && c.Anchor is not null && !c.IsOrphaned)
            .ToList();

        if (openRoots.Count == 0) return;

        // Running state: commentId → current anchor.
        var anchorState = openRoots.ToDictionary(c => c.Id, c => c.Anchor!);
        var orphaned = new HashSet<string>(StringComparer.Ordinal);

        long lastRebasedRevision = fromRevision - 1;

        await foreach (var op in documentStore.StreamOpsAsync(documentId, fromRevision - 1, ct))
        {
            if (op.Revision > upToRevision) break;

            var adapter = anchorRegistry.TryGet(op.EngineType);
            if (adapter is null)
            {
                // No anchor engine registered for this op type → anchors are unaffected.
                lastRebasedRevision = op.Revision;
                continue;
            }

            foreach (var c in openRoots)
            {
                if (orphaned.Contains(c.Id)) continue;
                if (c.AnchoredAtRevision >= op.Revision) continue;
                if (!anchorState.TryGetValue(c.Id, out var currentAnchor)) continue;

                AnchorRebaseResult result;
                try
                {
                    result = adapter.Rebase(currentAnchor, op.Payload);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Compact rebase threw for comment {CommentId} on {DocId} at revision {Rev}",
                        c.Id, documentId, op.Revision);
                    continue;
                }

                anchorState[c.Id] = result.Anchor;
                if (result.Outcome == AnchorOutcome.Orphaned)
                    orphaned.Add(c.Id);
            }

            lastRebasedRevision = op.Revision;
        }

        // Persist the final batch.
        var updates = openRoots
            .Where(c => anchorState.ContainsKey(c.Id))
            .Select(c => new AnchorUpdate(c.Id, anchorState[c.Id],
                orphaned.Contains(c.Id) ? AnchorOutcome.Orphaned : AnchorOutcome.Moved))
            .ToList();

        if (updates.Count == 0) return;

        var finalRevision = lastRebasedRevision > 0 ? lastRebasedRevision : upToRevision;
        await commentStore.UpdateAnchorsAsync(documentId, finalRevision, updates, ct);

        activity?.SetTag("comments.rebased_count", updates.Count);

        OpStreamTelemetry.CommentCompactRebaseLatency.RecordElapsedMs(sw,
            new KeyValuePair<string, object?>("comments.rebased_count", updates.Count));

        logger.LogInformation(
            "Compact rebase for {DocId}: {Total} anchors processed, {Orphaned} orphaned (up to revision {Rev})",
            documentId, updates.Count, orphaned.Count, finalRevision);
    }
}
