namespace OpStream.Server.Comments;

/// <summary>
/// Persistence contract for document comments. Operates on global document ids; multi-tenancy is
/// already resolved by the caller via the document id globalizer.
/// </summary>
public interface ICommentStore
{
    /// <summary>
    /// Returns every non-deleted comment for the document (root comments and their replies).
    /// Callers may filter resolved ones client-side.
    /// </summary>
    Task<IReadOnlyList<Comment>> LoadOpenAsync(string documentId, CancellationToken ct = default);

    /// <summary>Looks up a single comment by id.</summary>
    Task<Comment?> GetAsync(string commentId, CancellationToken ct = default);

    /// <summary>Persists a brand-new comment.</summary>
    Task AddAsync(Comment comment, CancellationToken ct = default);

    /// <summary>
    /// Replaces an existing comment in storage. Used for edits, resolves, and orphan marking.
    /// </summary>
    Task UpdateAsync(Comment comment, CancellationToken ct = default);

    /// <summary>Removes a comment (and any of its replies if this is a root) from storage.</summary>
    Task DeleteAsync(string commentId, CancellationToken ct = default);

    /// <summary>
    /// Atomically applies a batch of anchor updates produced by a rebase pass and bumps the
    /// affected comments' <c>AnchoredAtRevision</c> to <paramref name="revision"/>.
    /// </summary>
    Task UpdateAnchorsAsync(string documentId, long revision,
        IReadOnlyList<AnchorUpdate> updates, CancellationToken ct = default);

    /// <summary>
    /// Returns the lowest <c>AnchoredAtRevision</c> across non-resolved root comments for the
    /// document, or <c>null</c> if there are none. Used by compaction to know how far back the
    /// op log must remain replayable for anchor recovery.
    /// </summary>
    Task<long?> GetMinAnchoredRevisionAsync(string documentId, CancellationToken ct = default);
}
