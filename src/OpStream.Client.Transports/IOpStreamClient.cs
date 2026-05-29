using System.Text.Json;
using OpStream.Shared.Messages;

namespace OpStream.Client.Transports;

/// <summary>
/// Results of the initial handshake.
/// </summary>
public record ClientJoinResult(long Revision, ReadOnlyMemory<byte> Snapshot, IEnumerable<AwarenessState> Awareness);

/// <summary>
/// Results of a client operation submission.
/// </summary>
public record ClientOpResult(bool Success, long NewRevision, string? ErrorMessage);

/// <summary>
/// Transport abstraction so that UI clients do not depend on SignalR.
/// </summary>
public interface IOpStreamClient : IAsyncDisposable
{
    /// <summary>
    /// Event triggered when a remote operation arrives.
    /// Func(ReadOnlyMemory<byte> payload, long newRevision)
    /// </summary>
    event Func<ReadOnlyMemory<byte>, long, Task>? OnReceiveOp;

    /// <summary>
    /// Event triggered if the connection is lost.
    /// </summary>
    event Action<Exception?>? OnDisconnected;

    /// <summary>
    /// Starts the connection and performs the handshake with the document.
    /// </summary>
    Task<ClientJoinResult> ConnectAndJoinAsync(string documentId, string documentType, CancellationToken ct = default);

    /// <summary>
    /// Sends a local operation to the server.
    /// </summary>
    Task<ClientOpResult> SendOpAsync(string documentId, ReadOnlyMemory<byte> payload, long baseRevision, CancellationToken ct = default);

    /// <summary>
    /// Event triggered when awareness states are received.
    /// </summary>
    event Func<IEnumerable<AwarenessState>, Task>? OnReceiveAwareness;

    /// <summary>
    /// Event triggered when a peer disconnects.
    /// </summary>
    event Action<string>? OnPeerDisconnected;

    /// <summary>
    /// Sends awareness data to the server.
    /// </summary>
    Task SendAwarenessAsync(string documentId, JsonElement data, CancellationToken ct = default);

    // ─── Comments ────────────────────────────────────────────────────────────
    // The default implementations let transports that have not wired the comment
    // hub methods yet keep compiling. The SignalR transport overrides them all.

    /// <summary>Event triggered when a new comment is broadcast for the document.</summary>
    event Func<CommentDto, Task>? OnCommentCreated
    {
        add { }
        remove { }
    }

    /// <summary>Event triggered when an existing comment is edited, resolved or its anchor rebased.</summary>
    event Func<CommentDto, Task>? OnCommentUpdated
    {
        add { }
        remove { }
    }

    /// <summary>Event triggered when a comment is deleted.</summary>
    event Func<CommentDeletedDto, Task>? OnCommentDeleted
    {
        add { }
        remove { }
    }

    /// <summary>Returns every non-resolved comment (roots + replies) for the document.</summary>
    Task<List<CommentDto>> ListOpenCommentsAsync(string documentId, CancellationToken ct = default)
        => throw new NotSupportedException( "This transport does not expose comments." );

    /// <summary>Creates a new root comment (Anchor required) or a reply (ParentCommentId set, Anchor null).</summary>
    Task<CommentDto> CreateCommentAsync(string documentId, NewCommentCmd cmd, CancellationToken ct = default)
        => throw new NotSupportedException( "This transport does not expose comments." );

    /// <summary>Edits the body of an existing comment.</summary>
    Task<CommentDto> EditCommentAsync(string documentId, string commentId, string newBody, CancellationToken ct = default)
        => throw new NotSupportedException( "This transport does not expose comments." );

    /// <summary>Marks a comment as resolved.</summary>
    Task<CommentDto> ResolveCommentAsync(string documentId, string commentId, CancellationToken ct = default)
        => throw new NotSupportedException( "This transport does not expose comments." );

    /// <summary>Deletes a comment (cascades to replies when targeting a root).</summary>
    Task DeleteCommentAsync(string documentId, string commentId, CancellationToken ct = default)
        => throw new NotSupportedException( "This transport does not expose comments." );
}
