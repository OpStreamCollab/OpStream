using System.Text.Json;

namespace OpStream.Client.Transports;

/// <summary>
/// Wire-compatible view of a server-side <c>OpStream.Server.Comments.Comment</c>.
/// </summary>
public record CommentDto(
    string Id,
    string DocumentId,
    string? ParentCommentId,
    string AuthorPeerId,
    string AuthorName,
    string Body,
    AnchorDto? Anchor,
    long AnchoredAtRevision,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt,
    string? ResolvedByPeerId,
    bool IsOrphaned );

/// <summary>
/// Wire-compatible view of a comment anchor. <see cref="Kind"/> selects which
/// server-side anchor engine interprets <see cref="Data"/> (e.g. <c>"text"</c>,
/// <c>"richtext"</c>, <c>"json"</c>).
/// </summary>
public record AnchorDto( string Kind, JsonElement Data );

/// <summary>
/// Command used to create a new root comment or a reply.
/// </summary>
/// <param name="Body">Free-form text body.</param>
/// <param name="Anchor">Required for root comments, must be <c>null</c> for replies.</param>
/// <param name="ParentCommentId">Root id when posting a reply, <c>null</c> for root comments.</param>
public record NewCommentCmd( string Body, AnchorDto? Anchor, string? ParentCommentId );

/// <summary>
/// Carrier event sent when a comment is removed.
/// </summary>
public record CommentDeletedDto( string CommentId );
