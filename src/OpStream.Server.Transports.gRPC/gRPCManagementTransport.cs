using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using OpStream.Server.Session;
using OpStream.Shared.Abstractions;
using OpStream.Shared.Messages;
using OpStream.Shared.Messages.gRPC;

namespace OpStream.Server.Transports.gRPC;

/// <summary>
/// gRPC service that exposes the OpStream management surface (list / inspect / delete / compact / purge).
/// Every call is routed through <see cref="DatabaseCommandRouter"/>, which applies tenant scoping,
/// authorization, and owner-node routing — identical to the SignalR management hub.
/// </summary>
public class gRPCManagementTransport(DatabaseCommandRouter router) : OpStreamManagementService.OpStreamManagementServiceBase
{
    public override async Task<MgmtListDocumentsResponse> ListDocuments(
        MgmtListDocumentsRequest request, ServerCallContext context)
    {
        var query = new DocumentQuery(request.Skip, request.Take);
        var result = await router.ListDocumentsAsync(query, context.CancellationToken);
        ThrowIfFailed(result);

        var response = new MgmtListDocumentsResponse();
        foreach (var doc in result.Value!)
            response.Documents.Add(ToInfoProto(doc));
        return response;
    }

    public override async Task<MgmtGetDocumentInfoResponse> GetDocumentInfo(
        MgmtDocumentRequest request, ServerCallContext context)
    {
        var result = await router.GetDocumentInfoAsync(request.DocumentId, context.CancellationToken);
        ThrowIfFailed(result);

        return result.Value is null
            ? new MgmtGetDocumentInfoResponse { Found = false }
            : new MgmtGetDocumentInfoResponse { Found = true, DocumentInfo = ToInfoProto(result.Value) };
    }

    public override async Task<MgmtGetSnapshotResponse> GetSnapshot(
        MgmtDocumentRequest request, ServerCallContext context)
    {
        var result = await router.GetSnapshotAsync(request.DocumentId, context.CancellationToken);
        ThrowIfFailed(result);

        if (result.Value is null)
            return new MgmtGetSnapshotResponse { Found = false };

        return new MgmtGetSnapshotResponse
        {
            Found = true,
            Snapshot = new MgmtSnapshotProto
            {
                Revision  = result.Value.Revision,
                Timestamp = Timestamp.FromDateTimeOffset(result.Value.Timestamp),
                State     = Google.Protobuf.ByteString.CopyFrom(result.Value.State.Span)
            }
        };
    }

    public override async Task<MgmtOkResponse> DeleteDocument(
        MgmtDocumentRequest request, ServerCallContext context)
    {
        var result = await router.DeleteDocumentAsync(request.DocumentId, context.CancellationToken);
        ThrowIfFailed(result);
        return new MgmtOkResponse();
    }

    public override async Task<MgmtOkResponse> CompactDocument(
        MgmtRevisionRequest request, ServerCallContext context)
    {
        var result = await router.CompactDocumentAsync(request.DocumentId, request.UpToRevision, context.CancellationToken);
        ThrowIfFailed(result);
        return new MgmtOkResponse();
    }

    public override async Task<MgmtListMilestonesResponse> ListMilestones(
        MgmtDocumentRequest request, ServerCallContext context)
    {
        var result = await router.ListMilestonesAsync(request.DocumentId, context.CancellationToken);
        ThrowIfFailed(result);

        var response = new MgmtListMilestonesResponse();
        foreach (var m in result.Value!)
            response.Milestones.Add(ToMilestoneProto(m));
        return response;
    }

    public override async Task<MgmtOkResponse> PurgeHistory(
        MgmtRevisionRequest request, ServerCallContext context)
    {
        var result = await router.PurgeHistoryAsync(request.DocumentId, request.UpToRevision, context.CancellationToken);
        ThrowIfFailed(result);
        return new MgmtOkResponse();
    }

    public override async Task<MgmtCountResponse> PurgeTenant(
        MgmtEmptyRequest request, ServerCallContext context)
    {
        var result = await router.PurgeTenantAsync(context.CancellationToken);
        ThrowIfFailed(result);
        return new MgmtCountResponse { Count = result.Value };
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static void ThrowIfFailed<T>(OpResult<T> result)
    {
        if (!result.Success) throw ToRpcException(result.ErrorMessage);
    }

    private static void ThrowIfFailed(OpResult result)
    {
        if (!result.Success) throw ToRpcException(result.ErrorMessage);
    }

    private static RpcException ToRpcException(string? message)
    {
        var code = (message?.StartsWith("Forbidden:", StringComparison.Ordinal) ?? false)
            ? StatusCode.PermissionDenied
            : StatusCode.Internal;
        return new RpcException(new Status(code, message ?? "Unknown error"));
    }

    private static MgmtDocumentInfoProto ToInfoProto(DocumentInfo info) => new()
    {
        DocumentId   = info.DocumentId,
        Revision     = info.Revision,
        LastModified = Timestamp.FromDateTimeOffset(info.LastModified),
        OpCount      = info.OpCount
    };

    private static MgmtMilestoneProto ToMilestoneProto(HistoryMilestone m) => new()
    {
        Revision  = m.Revision,
        Timestamp = Timestamp.FromDateTimeOffset(m.Timestamp),
        Name      = m.Name ?? string.Empty
    };
}
