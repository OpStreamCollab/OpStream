namespace OpStream.Server.Comments;

/// <summary>
/// A comment attached to a document. Replies reference their root via <see cref="ParentCommentId"/>;
/// only root comments carry an <see cref="Anchor"/>.
/// </summary>
public record Comment(
    string Id,
    string DocumentId,
    string? ParentCommentId,
    string AuthorPeerId,
    string AuthorName,
    string Body,
    Anchor? Anchor,
    long AnchoredAtRevision,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt,
    string? ResolvedByPeerId,
    bool IsOrphaned
);

/// <summary>
/// Payload describing a new anchor position for a comment after a rebase pass.
/// </summary>
public record AnchorUpdate(string CommentId, Anchor Anchor, AnchorOutcome Outcome);

/// <summary>
/// Command sent by a client to create a new root comment or reply.
/// </summary>
/// <param name="Body">Free-form text body.</param>
/// <param name="Anchor">Required for root comments; must be <c>null</c> for replies.</param>
/// <param name="ParentCommentId">Set to the root comment id when posting a reply; <c>null</c> for root comments.</param>
public record NewCommentCmd(string Body, Anchor? Anchor, string? ParentCommentId);
