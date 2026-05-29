using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using OpStream.Server.Models;
using OpStream.Server.Versioning;
using OpStream.Shared.Abstractions;
using OpStream.Shared.Messages.gRPC;

namespace OpStream.Server.Transports.gRPC;

/// <summary>
/// gRPC service that exposes the OpStream versioning surface (names / branches / versions / merge).
/// Every call is routed through <see cref="VersioningRouter"/>, which applies tenant scoping
/// and authorization — identical to the SignalR versioning hub.
/// </summary>
public class gRPCVersioningTransport(VersioningRouter router) : OpStreamVersioningService.OpStreamVersioningServiceBase
{
    // ── Names ─────────────────────────────────────────────────────────────────

    public override async Task<VerNameInfoResponse> RegisterName(
        VerRegisterNameRequest request, ServerCallContext context)
    {
        var result = await router.RegisterNameAsync(
            request.Name,
            request.PhysicalDocumentId,
            request.EngineType,
            string.IsNullOrEmpty(request.RootBranchId) ? "main" : request.RootBranchId,
            context.CancellationToken);
        ThrowIfFailed(result);
        return new VerNameInfoResponse { NameInfo = ToNameProto(result.Value!) };
    }

    public override async Task<VerListNamesResponse> ListNames(
        VerEmptyRequest request, ServerCallContext context)
    {
        var result = await router.ListNamesAsync(context.CancellationToken);
        ThrowIfFailed(result);
        var response = new VerListNamesResponse();
        foreach (var n in result.Value!) response.Names.Add(ToNameProto(n));
        return response;
    }

    public override async Task<VerOkResponse> DeleteName(
        VerDeleteNameRequest request, ServerCallContext context)
    {
        var result = await router.DeleteNameAsync(request.Name, request.Cascade, context.CancellationToken);
        ThrowIfFailed(result);
        return new VerOkResponse();
    }

    // ── Branches ──────────────────────────────────────────────────────────────

    public override async Task<VerListBranchesResponse> ListBranches(
        VerNameRequest request, ServerCallContext context)
    {
        var result = await router.ListBranchesAsync(request.Name, context.CancellationToken);
        ThrowIfFailed(result);
        var response = new VerListBranchesResponse();
        foreach (var b in result.Value!) response.Branches.Add(ToBranchProto(b));
        return response;
    }

    public override async Task<VerBranchResponse> ForkBranch(
        VerForkBranchRequest request, ServerCallContext context)
    {
        long? atRevision = request.AtRevision;
        var result = await router.ForkBranchAsync(
            request.Name, request.FromBranchId, request.NewBranchId, atRevision, context.CancellationToken);
        ThrowIfFailed(result);
        return new VerBranchResponse { Branch = ToBranchProto(result.Value!) };
    }

    public override async Task<VerOkResponse> DeleteBranch(
        VerBranchRequest request, ServerCallContext context)
    {
        var result = await router.DeleteBranchAsync(request.Name, request.BranchId, context.CancellationToken);
        ThrowIfFailed(result);
        return new VerOkResponse();
    }

    // ── Versions / tags ───────────────────────────────────────────────────────

    public override async Task<VerVersionResponse> CreateVersion(
        VerCreateVersionRequest request, ServerCallContext context)
    {
        var result = await router.CreateVersionAsync(
            request.Name, request.BranchId, request.Tag, context.CancellationToken);
        ThrowIfFailed(result);
        return new VerVersionResponse { Version = ToVersionProto(result.Value!) };
    }

    public override async Task<VerListVersionsResponse> ListVersions(
        VerBranchRequest request, ServerCallContext context)
    {
        var result = await router.ListVersionsAsync(request.Name, request.BranchId, context.CancellationToken);
        ThrowIfFailed(result);
        var response = new VerListVersionsResponse();
        foreach (var v in result.Value!) response.Versions.Add(ToVersionProto(v));
        return response;
    }

    public override async Task<VerGetSnapshotResponse> ReadVersionSnapshot(
        VerVersionRequest request, ServerCallContext context)
    {
        var result = await router.ReadVersionSnapshotAsync(
            request.Name, request.BranchId, request.Tag, context.CancellationToken);
        ThrowIfFailed(result);

        if (result.Value is null) return new VerGetSnapshotResponse { Found = false };
        return new VerGetSnapshotResponse
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

    public override async Task<VerOkResponse> DeleteVersion(
        VerDeleteVersionRequest request, ServerCallContext context)
    {
        var result = await router.DeleteVersionAsync(
            request.Name, request.BranchId, request.Tag, request.DropSnapshot, context.CancellationToken);
        ThrowIfFailed(result);
        return new VerOkResponse();
    }

    // ── Merge ─────────────────────────────────────────────────────────────────

    public override async Task<VerMergeReportResponse> MergeBranch(
        VerMergeRequest request, ServerCallContext context)
    {
        var result = await router.MergeAsync(
            request.Name, request.TargetBranchId, request.SourceBranchId,
            dryRun: request.DryRun, ct: context.CancellationToken);
        ThrowIfFailed(result);
        return new VerMergeReportResponse { MergeReport = ToMergeProto(result.Value!) };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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

    private static VerNameInfoProto ToNameProto(DocumentNameInfo n) => new()
    {
        Name            = n.Name,
        DefaultBranchId = n.DefaultBranchId,
        EngineType      = n.EngineType,
        CreatedAt       = Timestamp.FromDateTimeOffset(n.CreatedAt)
    };

    private static VerBranchProto ToBranchProto(BranchRef b) => new()
    {
        Name                = b.Name,
        BranchId            = b.BranchId,
        PhysicalDocumentId  = b.PhysicalDocumentId,
        ForkParentBranchId  = b.ForkParentBranchId ?? string.Empty,
        ForkRevision        = b.ForkRevision,
        CreatedAt           = Timestamp.FromDateTimeOffset(b.CreatedAt),
        IsReadOnly          = b.IsReadOnly
    };

    private static VerVersionProto ToVersionProto(VersionRef v) => new()
    {
        Name                = v.Name,
        BranchId            = v.BranchId,
        Tag                 = v.Tag,
        Revision            = v.Revision,
        HistorySnapshotName = v.HistorySnapshotName,
        CreatedAt           = Timestamp.FromDateTimeOffset(v.CreatedAt)
    };

    private static VerMergeReportProto ToMergeProto(MergeReport r) => new()
    {
        SourceBranchId  = r.SourceBranchId,
        TargetBranchId  = r.TargetBranchId,
        RebasedOpCount  = r.RebasedOpCount,
        NullifiedOpCount = r.NullifiedOpCount,
        IsDryRun        = r.IsDryRun
    };
}
