using Grpc.Net.Client;
using OpStream.Client.Transports;
using OpStream.Server.Models;
using OpStream.Server.Versioning;
using OpStream.Shared.Messages.gRPC;

namespace OpStream.Client.Transports.gRPC;

/// <summary>
/// gRPC implementation of <see cref="IOpStreamManagementClient"/>.
/// Uses the generated <see cref="OpStreamManagementService"/> stub for the document/history plane
/// and the generated <see cref="OpStreamVersioningService"/> stub for the versioning plane.
/// "Forbidden:" errors from the server are surfaced as <see cref="OpStreamManagementException"/>
/// with <see cref="OpStreamManagementException.IsForbidden"/> set to true.
/// </summary>
public sealed class gRPCOpStreamManagementClient : IOpStreamManagementClient
{
    private readonly GrpcChannel _channel;
    private readonly OpStreamManagementService.OpStreamManagementServiceClient _mgmt;
    private readonly OpStreamVersioningService.OpStreamVersioningServiceClient _ver;

    public gRPCOpStreamManagementClient(string address)
    {
        _channel = GrpcChannel.ForAddress(address);
        _mgmt    = new OpStreamManagementService.OpStreamManagementServiceClient(_channel);
        _ver     = new OpStreamVersioningService.OpStreamVersioningServiceClient(_channel);
    }

    /// <inheritdoc/>
    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask; // gRPC is lazy-connect

    // ── Documents / history ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(DocumentQuery query, CancellationToken ct = default)
    {
        var request = new MgmtListDocumentsRequest();
        if (query.Skip.HasValue) request.Skip = query.Skip.Value;
        if (query.Take.HasValue) request.Take = query.Take.Value;

        var response = await CallAsync(() => _mgmt.ListDocumentsAsync(request, cancellationToken: ct));
        return response.Documents.Select(ToDocumentInfo).ToList();
    }

    /// <inheritdoc/>
    public async Task<DocumentInfo?> GetDocumentInfoAsync(string documentId, CancellationToken ct = default)
    {
        var response = await CallAsync(() => _mgmt.GetDocumentInfoAsync(
            new MgmtDocumentRequest { DocumentId = documentId }, cancellationToken: ct));
        return response.Found ? ToDocumentInfo(response.DocumentInfo) : null;
    }

    /// <inheritdoc/>
    public async Task<DocumentSnapshot?> GetSnapshotAsync(string documentId, CancellationToken ct = default)
    {
        var response = await CallAsync(() => _mgmt.GetSnapshotAsync(
            new MgmtDocumentRequest { DocumentId = documentId }, cancellationToken: ct));
        return response.Found ? ToSnapshot(response.Snapshot) : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<HistoryMilestone>> ListMilestonesAsync(string documentId, CancellationToken ct = default)
    {
        var response = await CallAsync(() => _mgmt.ListMilestonesAsync(
            new MgmtDocumentRequest { DocumentId = documentId }, cancellationToken: ct));
        return response.Milestones.Select(m => new HistoryMilestone(m.Revision, m.Timestamp.ToDateTimeOffset(), m.Name)).ToList();
    }

    /// <inheritdoc/>
    public async Task DeleteDocumentAsync(string documentId, CancellationToken ct = default)
        => await CallAsync(() => _mgmt.DeleteDocumentAsync(
            new MgmtDocumentRequest { DocumentId = documentId }, cancellationToken: ct));

    /// <inheritdoc/>
    public async Task CompactDocumentAsync(string documentId, long upToRevision, CancellationToken ct = default)
        => await CallAsync(() => _mgmt.CompactDocumentAsync(
            new MgmtRevisionRequest { DocumentId = documentId, UpToRevision = upToRevision }, cancellationToken: ct));

    /// <inheritdoc/>
    public async Task PurgeHistoryAsync(string documentId, long upToRevision, CancellationToken ct = default)
        => await CallAsync(() => _mgmt.PurgeHistoryAsync(
            new MgmtRevisionRequest { DocumentId = documentId, UpToRevision = upToRevision }, cancellationToken: ct));

    /// <inheritdoc/>
    public async Task<int> PurgeTenantAsync(CancellationToken ct = default)
    {
        var response = await CallAsync(() => _mgmt.PurgeTenantAsync(new MgmtEmptyRequest(), cancellationToken: ct));
        return response.Count;
    }

    // ── Names ─────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DocumentNameInfo>> ListNamesAsync(CancellationToken ct = default)
    {
        var response = await CallAsync(() => _ver.ListNamesAsync(new VerEmptyRequest(), cancellationToken: ct));
        return response.Names.Select(ToNameInfo).ToList();
    }

    /// <inheritdoc/>
    public async Task DeleteNameAsync(string name, bool cascade = false, CancellationToken ct = default)
        => await CallAsync(() => _ver.DeleteNameAsync(
            new VerDeleteNameRequest { Name = name, Cascade = cascade }, cancellationToken: ct));

    // ── Branches ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<IReadOnlyList<BranchRef>> ListBranchesAsync(string name, CancellationToken ct = default)
    {
        var response = await CallAsync(() => _ver.ListBranchesAsync(
            new VerNameRequest { Name = name }, cancellationToken: ct));
        return response.Branches.Select(ToBranchRef).ToList();
    }

    /// <inheritdoc/>
    public async Task<BranchRef> ForkBranchAsync(string name, string fromBranchId, string newBranchId,
        long? atRevision = null, CancellationToken ct = default)
    {
        var request = new VerForkBranchRequest
        {
            Name = name, FromBranchId = fromBranchId, NewBranchId = newBranchId
        };
        request.AtRevision = atRevision;

        var response = await CallAsync(() => _ver.ForkBranchAsync(request, cancellationToken: ct));
        return ToBranchRef(response.Branch);
    }

    /// <inheritdoc/>
    public async Task DeleteBranchAsync(string name, string branchId, CancellationToken ct = default)
        => await CallAsync(() => _ver.DeleteBranchAsync(
            new VerBranchRequest { Name = name, BranchId = branchId }, cancellationToken: ct));

    // ── Versions / tags ───────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<IReadOnlyList<VersionRef>> ListVersionsAsync(string name, string branchId, CancellationToken ct = default)
    {
        var response = await CallAsync(() => _ver.ListVersionsAsync(
            new VerBranchRequest { Name = name, BranchId = branchId }, cancellationToken: ct));
        return response.Versions.Select(ToVersionRef).ToList();
    }

    /// <inheritdoc/>
    public async Task<VersionRef> CreateVersionAsync(string name, string branchId, string tag, CancellationToken ct = default)
    {
        var response = await CallAsync(() => _ver.CreateVersionAsync(
            new VerCreateVersionRequest { Name = name, BranchId = branchId, Tag = tag }, cancellationToken: ct));
        return ToVersionRef(response.Version);
    }

    /// <inheritdoc/>
    public async Task<DocumentSnapshot?> ReadVersionSnapshotAsync(string name, string branchId, string tag,
        CancellationToken ct = default)
    {
        var response = await CallAsync(() => _ver.ReadVersionSnapshotAsync(
            new VerVersionRequest { Name = name, BranchId = branchId, Tag = tag }, cancellationToken: ct));
        return response.Found ? ToSnapshot(response.Snapshot) : null;
    }

    /// <inheritdoc/>
    public async Task DeleteVersionAsync(string name, string branchId, string tag,
        bool dropSnapshot = false, CancellationToken ct = default)
        => await CallAsync(() => _ver.DeleteVersionAsync(
            new VerDeleteVersionRequest { Name = name, BranchId = branchId, Tag = tag, DropSnapshot = dropSnapshot },
            cancellationToken: ct));

    // ── Merge ─────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<MergeReport> MergeBranchAsync(string name, string targetBranchId, string sourceBranchId,
        bool dryRun = false, CancellationToken ct = default)
    {
        var response = await CallAsync(() => _ver.MergeBranchAsync(
            new VerMergeRequest
            {
                Name = name, TargetBranchId = targetBranchId,
                SourceBranchId = sourceBranchId, DryRun = dryRun
            }, cancellationToken: ct));
        var r = response.MergeReport;
        return new MergeReport(r.SourceBranchId, r.TargetBranchId, r.RebasedOpCount, r.NullifiedOpCount, r.IsDryRun);
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _channel.ShutdownAsync();
        _channel.Dispose();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<T> CallAsync<T>(Func<Grpc.Core.AsyncUnaryCall<T>> call)
    {
        try
        {
            return await call();
        }
        catch (Grpc.Core.RpcException ex)
        {
            throw new OpStreamManagementException(ex.Status.Detail);
        }
    }

    private static DocumentInfo ToDocumentInfo(MgmtDocumentInfoProto p) =>
        new(p.DocumentId, p.Revision, p.LastModified.ToDateTimeOffset(), p.OpCount);

    private static DocumentSnapshot ToSnapshot(MgmtSnapshotProto p) =>
        new(p.Revision, p.Timestamp.ToDateTimeOffset(), p.State.Memory);

    private static DocumentNameInfo ToNameInfo(VerNameInfoProto p) =>
        new(p.Name, p.DefaultBranchId, p.EngineType, p.CreatedAt.ToDateTimeOffset());

    private static BranchRef ToBranchRef(VerBranchProto p) =>
        new(p.Name, p.BranchId, p.PhysicalDocumentId,
            string.IsNullOrEmpty(p.ForkParentBranchId) ? null : p.ForkParentBranchId,
            p.ForkRevision, p.CreatedAt.ToDateTimeOffset(), p.IsReadOnly);

    private static VersionRef ToVersionRef(VerVersionProto p) =>
        new(p.Name, p.BranchId, p.Tag, p.Revision, p.HistorySnapshotName, p.CreatedAt.ToDateTimeOffset());
}
