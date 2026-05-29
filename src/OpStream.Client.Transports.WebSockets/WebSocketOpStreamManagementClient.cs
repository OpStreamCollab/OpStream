using OpStream.Client.Transports;
using OpStream.Constants;
using OpStream.Server.Models;
using OpStream.Server.Versioning;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace OpStream.Client.Transports.WebSockets;

/// <summary>
/// WebSocket implementation of <see cref="IOpStreamManagementClient"/>.
/// Opens two independent WebSocket connections — one to the management endpoint
/// (speaks <c>WebSocketMgmtRequest</c> / <c>WebSocketMgmtResponse</c>) and one to
/// the versioning endpoint (speaks <c>WebSocketVerRequest</c> / <c>WebSocketVerResponse</c>).
/// </summary>
public sealed class WebSocketOpStreamManagementClient : IOpStreamManagementClient
{
    private readonly Uri _mgmtUri;
    private readonly Uri _verUri;

    private readonly ClientWebSocket _mgmtWs  = new();
    private readonly ClientWebSocket _verWs    = new();
    private readonly CancellationTokenSource _cts = new();

    private readonly ConcurrentDictionary<string, TaskCompletionSource<MgmtResponse>>
        _mgmtPending = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<VerResponse>>
        _verPending  = new();

    private Task? _mgmtListenTask;
    private Task? _verListenTask;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public WebSocketOpStreamManagementClient(
        string managementWsUri = "ws://localhost:5000/ws-mgmt",
        string versioningWsUri = "ws://localhost:5000/ws-versioning")
    {
        _mgmtUri = new Uri(managementWsUri);
        _verUri  = new Uri(versioningWsUri);
    }

    /// <inheritdoc/>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _mgmtWs.ConnectAsync(_mgmtUri, ct);
        await _verWs.ConnectAsync(_verUri,  ct);

        _mgmtListenTask = ListenMgmtAsync(_cts.Token);
        _verListenTask  = ListenVerAsync(_cts.Token);
    }

    // ── Listen loops ─────────────────────────────────────────────────────────

    private async Task ListenMgmtAsync(CancellationToken ct)
    {
        try
        {
            while (_mgmtWs.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var json = await ReceiveTextAsync(_mgmtWs, ct);
                if (json is null) break;

                var msg = JsonSerializer.Deserialize<MgmtResponse>(json, JsonOpts);
                if (msg?.CorrelationId is not null &&
                    _mgmtPending.TryRemove(msg.CorrelationId, out var tcs))
                    tcs.SetResult(msg);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ListenVerAsync(CancellationToken ct)
    {
        try
        {
            while (_verWs.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var json = await ReceiveTextAsync(_verWs, ct);
                if (json is null) break;

                var msg = JsonSerializer.Deserialize<VerResponse>(json, JsonOpts);
                if (msg?.CorrelationId is not null &&
                    _verPending.TryRemove(msg.CorrelationId, out var tcs))
                    tcs.SetResult(msg);
            }
        }
        catch (OperationCanceledException) { }
    }

    // ── Request helpers ───────────────────────────────────────────────────────

    private async Task<MgmtResponse> SendMgmtAsync(MgmtRequest request, CancellationToken ct)
    {
        var correlationId = Guid.NewGuid().ToString();
        request.CorrelationId = correlationId;

        var tcs = new TaskCompletionSource<MgmtResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mgmtPending[correlationId] = tcs;

        await SendAsync(_mgmtWs, request, ct);

        using var reg = ct.Register(() => tcs.TrySetCanceled());
        var response = await tcs.Task;

        if (!response.Success)
            throw new OpStreamManagementException(response.ErrorMessage ?? "Management operation failed.");

        return response;
    }

    private async Task<VerResponse> SendVerAsync(VerRequest request, CancellationToken ct)
    {
        var correlationId = Guid.NewGuid().ToString();
        request.CorrelationId = correlationId;

        var tcs = new TaskCompletionSource<VerResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _verPending[correlationId] = tcs;

        await SendAsync(_verWs, request, ct);

        using var reg = ct.Register(() => tcs.TrySetCanceled());
        var response = await tcs.Task;

        if (!response.Success)
            throw new OpStreamManagementException(response.ErrorMessage ?? "Versioning operation failed.");

        return response;
    }

    private async Task SendAsync<T>(ClientWebSocket ws, T payload, CancellationToken ct)
    {
        var json  = JsonSerializer.Serialize(payload, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    // ── Documents / history ───────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(DocumentQuery query, CancellationToken ct = default)
    {
        var r = await SendMgmtAsync(new MgmtRequest
        {
            Command = OpStreamConstants.ManagementHubMethods.ListDocuments,
            Skip = query.Skip, Take = query.Take
        }, ct);
        return r.Documents ?? [];
    }

    /// <inheritdoc/>
    public async Task<DocumentInfo?> GetDocumentInfoAsync(string documentId, CancellationToken ct = default)
    {
        var r = await SendMgmtAsync(new MgmtRequest
        {
            Command = OpStreamConstants.ManagementHubMethods.GetDocumentInfo,
            DocumentId = documentId
        }, ct);
        return r.DocumentInfo;
    }

    /// <inheritdoc/>
    public async Task<DocumentSnapshot?> GetSnapshotAsync(string documentId, CancellationToken ct = default)
    {
        var r = await SendMgmtAsync(new MgmtRequest
        {
            Command = OpStreamConstants.ManagementHubMethods.GetSnapshot,
            DocumentId = documentId
        }, ct);
        return r.Snapshot;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<HistoryMilestone>> ListMilestonesAsync(string documentId, CancellationToken ct = default)
    {
        var r = await SendMgmtAsync(new MgmtRequest
        {
            Command = OpStreamConstants.ManagementHubMethods.ListMilestones,
            DocumentId = documentId
        }, ct);
        return r.Milestones ?? [];
    }

    /// <inheritdoc/>
    public async Task DeleteDocumentAsync(string documentId, CancellationToken ct = default)
        => await SendMgmtAsync(new MgmtRequest
        {
            Command = OpStreamConstants.ManagementHubMethods.DeleteDocument,
            DocumentId = documentId
        }, ct);

    /// <inheritdoc/>
    public async Task CompactDocumentAsync(string documentId, long upToRevision, CancellationToken ct = default)
        => await SendMgmtAsync(new MgmtRequest
        {
            Command = OpStreamConstants.ManagementHubMethods.CompactDocument,
            DocumentId = documentId, UpToRevision = upToRevision
        }, ct);

    /// <inheritdoc/>
    public async Task PurgeHistoryAsync(string documentId, long upToRevision, CancellationToken ct = default)
        => await SendMgmtAsync(new MgmtRequest
        {
            Command = OpStreamConstants.ManagementHubMethods.PurgeHistory,
            DocumentId = documentId, UpToRevision = upToRevision
        }, ct);

    /// <inheritdoc/>
    public async Task<int> PurgeTenantAsync(CancellationToken ct = default)
    {
        var r = await SendMgmtAsync(new MgmtRequest
        {
            Command = OpStreamConstants.ManagementHubMethods.PurgeTenant
        }, ct);
        return r.Count ?? 0;
    }

    // ── Names ─────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DocumentNameInfo>> ListNamesAsync(CancellationToken ct = default)
    {
        var r = await SendVerAsync(new VerRequest { Command = OpStreamConstants.VersioningHubMethods.ListNames }, ct);
        return r.Names ?? [];
    }

    /// <inheritdoc/>
    public async Task DeleteNameAsync(string name, bool cascade = false, CancellationToken ct = default)
        => await SendVerAsync(new VerRequest
        {
            Command = OpStreamConstants.VersioningHubMethods.DeleteName,
            Name = name, Cascade = cascade
        }, ct);

    // ── Branches ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<IReadOnlyList<BranchRef>> ListBranchesAsync(string name, CancellationToken ct = default)
    {
        var r = await SendVerAsync(new VerRequest
        {
            Command = OpStreamConstants.VersioningHubMethods.ListBranches,
            Name = name
        }, ct);
        return r.Branches ?? [];
    }

    /// <inheritdoc/>
    public async Task<BranchRef> ForkBranchAsync(string name, string fromBranchId, string newBranchId,
        long? atRevision = null, CancellationToken ct = default)
    {
        var r = await SendVerAsync(new VerRequest
        {
            Command = OpStreamConstants.VersioningHubMethods.ForkBranch,
            Name = name, FromBranchId = fromBranchId, NewBranchId = newBranchId, AtRevision = atRevision
        }, ct);
        return r.Branch ?? throw new OpStreamManagementException("Server returned no branch data.");
    }

    /// <inheritdoc/>
    public async Task DeleteBranchAsync(string name, string branchId, CancellationToken ct = default)
        => await SendVerAsync(new VerRequest
        {
            Command = OpStreamConstants.VersioningHubMethods.DeleteBranch,
            Name = name, BranchId = branchId
        }, ct);

    // ── Versions / tags ───────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<IReadOnlyList<VersionRef>> ListVersionsAsync(string name, string branchId, CancellationToken ct = default)
    {
        var r = await SendVerAsync(new VerRequest
        {
            Command = OpStreamConstants.VersioningHubMethods.ListVersions,
            Name = name, BranchId = branchId
        }, ct);
        return r.Versions ?? [];
    }

    /// <inheritdoc/>
    public async Task<VersionRef> CreateVersionAsync(string name, string branchId, string tag, CancellationToken ct = default)
    {
        var r = await SendVerAsync(new VerRequest
        {
            Command = OpStreamConstants.VersioningHubMethods.CreateVersion,
            Name = name, BranchId = branchId, Tag = tag
        }, ct);
        return r.Version ?? throw new OpStreamManagementException("Server returned no version data.");
    }

    /// <inheritdoc/>
    public async Task<DocumentSnapshot?> ReadVersionSnapshotAsync(string name, string branchId, string tag,
        CancellationToken ct = default)
    {
        var r = await SendVerAsync(new VerRequest
        {
            Command = OpStreamConstants.VersioningHubMethods.ReadVersionSnapshot,
            Name = name, BranchId = branchId, Tag = tag
        }, ct);
        return r.Snapshot;
    }

    /// <inheritdoc/>
    public async Task DeleteVersionAsync(string name, string branchId, string tag,
        bool dropSnapshot = false, CancellationToken ct = default)
        => await SendVerAsync(new VerRequest
        {
            Command = OpStreamConstants.VersioningHubMethods.DeleteVersion,
            Name = name, BranchId = branchId, Tag = tag, DropSnapshot = dropSnapshot
        }, ct);

    // ── Merge ─────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<MergeReport> MergeBranchAsync(string name, string targetBranchId, string sourceBranchId,
        bool dryRun = false, CancellationToken ct = default)
    {
        var command = dryRun
            ? OpStreamConstants.VersioningHubMethods.DryRunMerge
            : OpStreamConstants.VersioningHubMethods.MergeBranch;
        var r = await SendVerAsync(new VerRequest
        {
            Command = command,
            Name = name, TargetBranchId = targetBranchId, SourceBranchId = sourceBranchId, DryRun = dryRun
        }, ct);
        return r.MergeReport ?? throw new OpStreamManagementException("Server returned no merge report.");
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var ws in new[] { _mgmtWs, _verWs })
        {
            if (ws.State == WebSocketState.Open)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", CancellationToken.None); }
                catch { }
            }
            ws.Dispose();
        }
        if (_mgmtListenTask is not null) try { await _mgmtListenTask; } catch { }
        if (_verListenTask  is not null) try { await _verListenTask;  } catch { }
        _cts.Dispose();
    }

    // ── Wire protocol DTOs (mirror WebSocketMgmtRequest/Response and WebSocketVerRequest/Response) ─

    private class MgmtRequest
    {
        public string? CorrelationId { get; set; }
        public required string Command { get; set; }
        public int?    Skip           { get; set; }
        public int?    Take           { get; set; }
        public string? DocumentId     { get; set; }
        public long?   UpToRevision   { get; set; }
    }

    private class MgmtResponse
    {
        public string?                         CorrelationId { get; set; }
        public bool                            Success       { get; set; }
        public string?                         ErrorMessage  { get; set; }
        public IReadOnlyList<DocumentInfo>?    Documents     { get; set; }
        public DocumentInfo?                   DocumentInfo  { get; set; }
        public DocumentSnapshot?               Snapshot      { get; set; }
        public IReadOnlyList<HistoryMilestone>? Milestones   { get; set; }
        public int?                            Count         { get; set; }
    }

    private class VerRequest
    {
        public string? CorrelationId  { get; set; }
        public required string Command { get; set; }
        public string? Name           { get; set; }
        public string? BranchId       { get; set; }
        public string? FromBranchId   { get; set; }
        public string? NewBranchId    { get; set; }
        public string? Tag            { get; set; }
        public string? TargetBranchId { get; set; }
        public string? SourceBranchId { get; set; }
        public long?   AtRevision     { get; set; }
        public bool    Cascade        { get; set; }
        public bool    DropSnapshot   { get; set; }
        public bool    DryRun         { get; set; }
    }

    private class VerResponse
    {
        public string?                           CorrelationId { get; set; }
        public bool                              Success       { get; set; }
        public string?                           ErrorMessage  { get; set; }
        public DocumentNameInfo?                 NameInfo      { get; set; }
        public IReadOnlyList<DocumentNameInfo>?  Names         { get; set; }
        public BranchRef?                        Branch        { get; set; }
        public IReadOnlyList<BranchRef>?         Branches      { get; set; }
        public VersionRef?                       Version       { get; set; }
        public IReadOnlyList<VersionRef>?        Versions      { get; set; }
        public DocumentSnapshot?                 Snapshot      { get; set; }
        public MergeReport?                      MergeReport   { get; set; }
    }

    // ─── Shared receive helper ────────────────────────────────────────────────

    private static async Task<string?> ReceiveTextAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[1024 * 4];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        ms.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(ms, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
    }
}
