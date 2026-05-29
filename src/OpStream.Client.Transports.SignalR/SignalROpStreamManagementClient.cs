using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using OpStream.Client.Transports;
using OpStream.Constants;
using OpStream.Shared.Messages;

namespace OpStream.Client.Transports.SignalR;

/// <summary>
/// SignalR implementation of <see cref="IOpStreamManagementClient"/>.
/// Opens two hub connections — one to the management hub, one to the versioning hub —
/// and delegates every call through the corresponding <see cref="OpStreamConstants"/> method names.
/// </summary>
public class SignalROpStreamManagementClient : IOpStreamManagementClient
{
    private readonly HubConnection _mgmtHub;
    private readonly HubConnection _verHub;

    /// <param name="managementHubUrl">URL of the management SignalR hub (default: /mgmt).</param>
    /// <param name="versioningHubUrl">URL of the versioning SignalR hub (default: /versioning).</param>
    public SignalROpStreamManagementClient(string managementHubUrl = "/mgmt", string versioningHubUrl = "/versioning")
    {
        _mgmtHub = new HubConnectionBuilder()
            .WithUrl(managementHubUrl)
            .WithAutomaticReconnect()
            .Build();

        _verHub = new HubConnectionBuilder()
            .WithUrl(versioningHubUrl)
            .WithAutomaticReconnect()
            .Build();
    }

    /// <inheritdoc/>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _mgmtHub.StartAsync(ct);
        await _verHub.StartAsync(ct);
    }

    // ── Documents / history ──────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(DocumentQuery query, CancellationToken ct = default)
    {
        var result = await _mgmtHub.InvokeAsync<List<DocumentInfo>>(
            OpStreamConstants.ManagementHubMethods.ListDocuments,
            query.Skip, query.Take, cancellationToken: ct);
        return result ?? [];
    }

    /// <inheritdoc/>
    public Task<DocumentInfo?> GetDocumentInfoAsync(string documentId, CancellationToken ct = default)
        => _mgmtHub.InvokeAsync<DocumentInfo?>(
            OpStreamConstants.ManagementHubMethods.GetDocumentInfo,
            documentId, cancellationToken: ct);

    /// <inheritdoc/>
    public Task<DocumentSnapshot?> GetSnapshotAsync(string documentId, CancellationToken ct = default)
        => _mgmtHub.InvokeAsync<DocumentSnapshot?>(
            OpStreamConstants.ManagementHubMethods.GetSnapshot,
            documentId, cancellationToken: ct);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<HistoryMilestone>> ListMilestonesAsync(string documentId, CancellationToken ct = default)
    {
        var result = await _mgmtHub.InvokeAsync<List<HistoryMilestone>>(
            OpStreamConstants.ManagementHubMethods.ListMilestones,
            documentId, cancellationToken: ct);
        return result ?? [];
    }

    /// <inheritdoc/>
    public Task DeleteDocumentAsync(string documentId, CancellationToken ct = default)
        => _mgmtHub.InvokeAsync(
            OpStreamConstants.ManagementHubMethods.DeleteDocument,
            documentId, cancellationToken: ct);

    /// <inheritdoc/>
    public Task CompactDocumentAsync(string documentId, long upToRevision, CancellationToken ct = default)
        => _mgmtHub.InvokeAsync(
            OpStreamConstants.ManagementHubMethods.CompactDocument,
            documentId, upToRevision, cancellationToken: ct);

    /// <inheritdoc/>
    public Task PurgeHistoryAsync(string documentId, long upToRevision, CancellationToken ct = default)
        => _mgmtHub.InvokeAsync(
            OpStreamConstants.ManagementHubMethods.PurgeHistory,
            documentId, upToRevision, cancellationToken: ct);

    /// <inheritdoc/>
    public Task<int> PurgeTenantAsync(CancellationToken ct = default)
        => _mgmtHub.InvokeAsync<int>(
            OpStreamConstants.ManagementHubMethods.PurgeTenant,
            cancellationToken: ct);

    // ── Names ────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DocumentNameInfo>> ListNamesAsync(CancellationToken ct = default)
    {
        var result = await _verHub.InvokeAsync<List<DocumentNameInfo>>(
            OpStreamConstants.VersioningHubMethods.ListNames,
            cancellationToken: ct);
        return result ?? [];
    }

    /// <inheritdoc/>
    public Task DeleteNameAsync(string name, bool cascade = false, CancellationToken ct = default)
        => _verHub.InvokeAsync(
            OpStreamConstants.VersioningHubMethods.DeleteName,
            name, cascade, cancellationToken: ct);

    // ── Branches ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<IReadOnlyList<BranchRef>> ListBranchesAsync(string name, CancellationToken ct = default)
    {
        var result = await _verHub.InvokeAsync<List<BranchRef>>(
            OpStreamConstants.VersioningHubMethods.ListBranches,
            name, cancellationToken: ct);
        return result ?? [];
    }

    /// <inheritdoc/>
    public Task<BranchRef> ForkBranchAsync(string name, string fromBranchId, string newBranchId,
        long? atRevision = null, CancellationToken ct = default)
        => _verHub.InvokeAsync<BranchRef>(
            OpStreamConstants.VersioningHubMethods.ForkBranch,
            name, fromBranchId, newBranchId, atRevision, cancellationToken: ct);

    /// <inheritdoc/>
    public Task DeleteBranchAsync(string name, string branchId, CancellationToken ct = default)
        => _verHub.InvokeAsync(
            OpStreamConstants.VersioningHubMethods.DeleteBranch,
            name, branchId, cancellationToken: ct);

    // ── Versions / tags ───────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<IReadOnlyList<VersionRef>> ListVersionsAsync(string name, string branchId, CancellationToken ct = default)
    {
        var result = await _verHub.InvokeAsync<List<VersionRef>>(
            OpStreamConstants.VersioningHubMethods.ListVersions,
            name, branchId, cancellationToken: ct);
        return result ?? [];
    }

    /// <inheritdoc/>
    public Task<VersionRef> CreateVersionAsync(string name, string branchId, string tag, CancellationToken ct = default)
        => _verHub.InvokeAsync<VersionRef>(
            OpStreamConstants.VersioningHubMethods.CreateVersion,
            name, branchId, tag, cancellationToken: ct);

    /// <inheritdoc/>
    public Task<DocumentSnapshot?> ReadVersionSnapshotAsync(string name, string branchId, string tag, CancellationToken ct = default)
        => _verHub.InvokeAsync<DocumentSnapshot?>(
            OpStreamConstants.VersioningHubMethods.ReadVersionSnapshot,
            name, branchId, tag, cancellationToken: ct);

    /// <inheritdoc/>
    public Task DeleteVersionAsync(string name, string branchId, string tag,
        bool dropSnapshot = false, CancellationToken ct = default)
        => _verHub.InvokeAsync(
            OpStreamConstants.VersioningHubMethods.DeleteVersion,
            name, branchId, tag, dropSnapshot, cancellationToken: ct);

    // ── Merge ─────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<MergeReport> MergeBranchAsync(string name, string targetBranchId, string sourceBranchId,
        bool dryRun = false, CancellationToken ct = default)
    {
        var method = dryRun
            ? OpStreamConstants.VersioningHubMethods.DryRunMerge
            : OpStreamConstants.VersioningHubMethods.MergeBranch;
        return _verHub.InvokeAsync<MergeReport>(
            method, name, targetBranchId, sourceBranchId, cancellationToken: ct);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _mgmtHub.DisposeAsync();
        await _verHub.DisposeAsync();
    }
}
