using Microsoft.AspNetCore.SignalR;
using OpStream.Constants;
using OpStream.Server.Multitenancy;
using OpStream.Server.Session;
using System.Text.Json;

namespace OpStream.Server.Transports.SignalR;

/// <summary>
/// The bridge between SignalR clients and in-memory sessions.
/// </summary>
public class SignalRTransport(DocumentRouter router, IDocumentIdGlobalizer globalizer) : Hub
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

        var result = await router.JoinDocumentAsync(globalDocId, documentType, peerId, clientProtoVersion);
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

        var result = await router.ApplyOpAsync(peerId, documentId: globalDocId, payload: payload, baseRevision: baseRevision, ct: Context.ConnectionAborted);
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

        await router.UpdateAwarenessAsync(peerId, globalDocId, data,ct: Context.ConnectionAborted);

        // Broadcast is now handled via Backplane -> HandleBackplaneMessageAsync
    }

    /// <summary>
    /// Handles the client disconnection.
    /// </summary>
    /// <param name="exception">The exception that caused the disconnection, if any.</param>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var peerId = Context.ConnectionId;

        // Disconnect the peer from all sessions they were part of
        await router.RemovePeerFromAllSessionsAsync(peerId);

        var globalDocIds = router.GetDocumentsId(peerId);
        foreach (var globalDocId in globalDocIds)
        {
            await Groups.RemoveFromGroupAsync(peerId, globalDocId);

            await Clients.Group(globalDocId).SendAsync(OpStreamConstants.ClientEvents.PeerDisconnected, peerId);
        }
        await base.OnDisconnectedAsync(exception);
    }
}
