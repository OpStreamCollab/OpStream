using Microsoft.AspNetCore.SignalR;
using OpStream.Constants;
using OpStream.Server.Comments;
using OpStream.Server.Multitenancy;
using OpStream.Server.Session;
using System.Text.Json;

namespace OpStream.Server.Transports.SignalR;

/// <summary>
/// The bridge between SignalR clients and in-memory sessions.
/// </summary>
public class SignalRTransport(DocumentRouter router, IDocumentIdGlobalizer globalizer, CommentRouter commentRouter) : Hub
{
    /// <summary>
    /// Called by the client when opening a document. (The Handshake).
    /// </summary>
    /// <param name="documentId">The ID of the document to join.</param>
    /// <param name="documentType">The type of the document.</param>
    /// <param name="clientProtoVersion">The protocol version used by the client.</param>
    /// <returns>A result containing initial session information.</returns>
    [HubMethodName(OpStreamConstants.HubMethods.JoinDocument)]
    public async Task<SessionJoinResult> JoinDocument(string documentId, string documentType, int clientProtoVersion)
    {       
        string globalDocId = globalizer.ToGlobalId(documentId);

        var peerId = Context.ConnectionId;

        var result = await router.JoinDocumentAsync(documentId, documentType, peerId, clientProtoVersion);
        if (!result.Success) throw new HubException(result.ErrorMessage);

        await Groups.AddToGroupAsync(peerId, globalDocId);

        return result.Value!;
    }

    /// <summary>
    /// Called by the client to send an operation.
    /// </summary>
    /// <param name="documentId">The ID of the document to apply the operation to.</param>
    /// <param name="payload">The operation payload.</param>
    /// <param name="baseRevision">The revision the operation is based on.</param>
    /// <returns>The result of applying the operation.</returns>
    [HubMethodName(OpStreamConstants.HubMethods.SendOp)]
    public async Task<OpApplyResult> SendOp(string documentId, byte[] payload, long baseRevision)
    {
        string globalDocId = globalizer.ToGlobalId(documentId);

        var peerId = Context.ConnectionId;

        var result = await router.ApplyOpAsync(peerId, documentId: documentId, payload: payload, baseRevision: baseRevision, ct: Context.ConnectionAborted);
        if (!result.Success) throw new HubException(result.ErrorMessage);

        // Broadcast is now handled via Backplane -> HandleBackplaneMessageAsync
        return result.Value!;
    }

    /// <summary>
    /// Receives the awareness state from a client and broadcasts it to others.
    /// </summary>
    /// <param name="documentId">The ID of the document associated with the awareness update.</param>
    /// <param name="data">The awareness data.</param>
    [HubMethodName(OpStreamConstants.HubMethods.UpdateAwareness)]
    public async Task UpdateAwareness(string documentId, JsonElement data)
    {
        string globalDocId = globalizer.ToGlobalId(documentId);

        var peerId = Context.ConnectionId;

        await router.UpdateAwarenessAsync(peerId, documentId, data,ct: Context.ConnectionAborted);

        // Broadcast is now handled via Backplane -> HandleBackplaneMessageAsync
    }

    // ─── Comments ─────────────────────────────────────────────────────────────

    /// <summary>Creates a new root comment or reply on a document.</summary>
    [HubMethodName(OpStreamConstants.HubMethods.CreateComment)]
    public async Task<Comment> CreateComment(string documentId, NewCommentCmd cmd)
    {
        var result = await commentRouter.CreateAsync(Context.ConnectionId, documentId, cmd, Context.ConnectionAborted);
        if (!result.Success) throw new HubException(result.ErrorMessage);
        return result.Value!;
    }

    /// <summary>Edits the body of an existing comment.</summary>
    [HubMethodName(OpStreamConstants.HubMethods.EditComment)]
    public async Task<Comment> EditComment(string documentId, string commentId, string newBody)
    {
        var result = await commentRouter.EditAsync(Context.ConnectionId, documentId, commentId, newBody, Context.ConnectionAborted);
        if (!result.Success) throw new HubException(result.ErrorMessage);
        return result.Value!;
    }

    /// <summary>Marks a comment as resolved.</summary>
    [HubMethodName(OpStreamConstants.HubMethods.ResolveComment)]
    public async Task<Comment> ResolveComment(string documentId, string commentId)
    {
        var result = await commentRouter.ResolveAsync(Context.ConnectionId, documentId, commentId, Context.ConnectionAborted);
        if (!result.Success) throw new HubException(result.ErrorMessage);
        return result.Value!;
    }

    /// <summary>Deletes a comment (and cascades to its replies if the target is a root).</summary>
    [HubMethodName(OpStreamConstants.HubMethods.DeleteComment)]
    public async Task DeleteComment(string documentId, string commentId)
    {
        var result = await commentRouter.DeleteAsync(Context.ConnectionId, documentId, commentId, Context.ConnectionAborted);
        if (!result.Success) throw new HubException(result.ErrorMessage);
    }

    /// <summary>Returns every non-deleted comment for the document (roots + replies).</summary>
    [HubMethodName(OpStreamConstants.HubMethods.ListOpenComments)]
    public async Task<IReadOnlyList<Comment>> ListOpenComments(string documentId)
    {
        var result = await commentRouter.ListOpenAsync(documentId, Context.ConnectionAborted);
        if (!result.Success) throw new HubException(result.ErrorMessage);
        return result.Value!;
    }

    /// <summary>
    /// Handles the client disconnection.
    /// </summary>
    /// <param name="exception">The exception that caused the disconnection, if any.</param>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var peerId = Context.ConnectionId;

        // Disconnect the peer from all sessions they were part of
        // This now notifies the backplane, which will trigger the broadcast on all nodes (including this one)
        await router.RemovePeerFromAllSessionsAsync(peerId);

        await base.OnDisconnectedAsync(exception);
    }
}
