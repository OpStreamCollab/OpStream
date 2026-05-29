using Microsoft.AspNetCore.SignalR;
using OpStream.Constants;
using OpStream.Server.Session;
using OpStream.Shared.Messages;

namespace OpStream.Server.Transports.SignalR;

/// <summary>
/// Management surface exposed over SignalR. Mirrors the document hub: every call goes through
/// <see cref="DatabaseCommandRouter"/>, which applies tenant globalization, authorization, and
/// owner-node routing.
/// <para>
/// Callers operate on <em>local</em> document ids — tenant scoping is implicit through
/// <see cref="OpStream.Shared.Abstractions.ITenantProvider"/>.
/// </para>
/// </summary>
public class SignalRManagementTransport(DatabaseCommandRouter router) : Hub
{
    [HubMethodName(OpStreamConstants.ManagementHubMethods.ListDocuments)]
    public async Task<IReadOnlyList<DocumentInfo>> ListDocuments(int? skip, int? take)
    {
        var result = await router.ListDocumentsAsync(new DocumentQuery(skip, take), Context.ConnectionAborted);
        if (!result.Success) throw new HubException(result.ErrorMessage);
        return result.Value!;
    }

    [HubMethodName(OpStreamConstants.ManagementHubMethods.GetDocumentInfo)]
    public async Task<DocumentInfo?> GetDocumentInfo(string documentId)
    {
        var result = await router.GetDocumentInfoAsync(documentId, Context.ConnectionAborted);
        if (!result.Success) throw new HubException(result.ErrorMessage);
        return result.Value;
    }

    [HubMethodName(OpStreamConstants.ManagementHubMethods.GetSnapshot)]
    public async Task<DocumentSnapshot?> GetSnapshot(string documentId)
    {
        var result = await router.GetSnapshotAsync(documentId, Context.ConnectionAborted);
        if (!result.Success) throw new HubException(result.ErrorMessage);
        return result.Value;
    }

    [HubMethodName(OpStreamConstants.ManagementHubMethods.ListMilestones)]
    public async Task<IReadOnlyList<HistoryMilestone>> ListMilestones(string documentId)
    {
        var result = await router.ListMilestonesAsync(documentId, Context.ConnectionAborted);
        if (!result.Success) throw new HubException(result.ErrorMessage);
        return result.Value!;
    }

    [HubMethodName(OpStreamConstants.ManagementHubMethods.DeleteDocument)]
    public async Task DeleteDocument(string documentId)
    {
        var result = await router.DeleteDocumentAsync(documentId, Context.ConnectionAborted);
        if (!result.Success) throw new HubException(result.ErrorMessage);
    }

    [HubMethodName(OpStreamConstants.ManagementHubMethods.CompactDocument)]
    public async Task CompactDocument(string documentId, long upToRevision)
    {
        var result = await router.CompactDocumentAsync(documentId, upToRevision, Context.ConnectionAborted);
        if (!result.Success) throw new HubException(result.ErrorMessage);
    }

    [HubMethodName(OpStreamConstants.ManagementHubMethods.PurgeHistory)]
    public async Task PurgeHistory(string documentId, long upToRevision)
    {
        var result = await router.PurgeHistoryAsync(documentId, upToRevision, Context.ConnectionAborted);
        if (!result.Success) throw new HubException(result.ErrorMessage);
    }

    [HubMethodName(OpStreamConstants.ManagementHubMethods.PurgeTenant)]
    public async Task<int> PurgeTenant()
    {
        var result = await router.PurgeTenantAsync(Context.ConnectionAborted);
        if (!result.Success) throw new HubException(result.ErrorMessage);
        return result.Value;
    }
}
