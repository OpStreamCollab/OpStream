using Microsoft.AspNetCore.SignalR;
using OpStream.Constants;
using OpStream.Server.Models;
using OpStream.Server.Versioning;

namespace OpStream.Server.Transports.SignalR;

/// <summary>
/// SignalR hub exposing the versioning control plane: document names, branches, version tags, and merge.
/// All operations go through <see cref="VersioningRouter"/>, which applies tenant globalization and
/// optional authorization.
/// </summary>
public class SignalRVersioningTransport(VersioningRouter router) : Hub
{
    // ─── Names ───────────────────────────────────────────────────────────────

    [HubMethodName(OpStreamConstants.VersioningHubMethods.RegisterName)]
    public async Task<DocumentNameInfo> RegisterName(
        string name, string physicalDocumentId, string engineType, string rootBranchId = "main")
    {
        var result = await router.RegisterNameAsync(
            name, physicalDocumentId, engineType, rootBranchId, Context.ConnectionAborted);
        if (!result.Success) throw new HubException(result.ErrorMessage);
        return result.Value!;
    }

    [HubMethodName(OpStreamConstants.VersioningHubMethods.ListNames)]
    public async Task<IReadOnlyList<DocumentNameInfo>> ListNames()
    {
        var result = await router.ListNamesAsync(Context.ConnectionAborted);
        if (!result.Success) throw new HubException(result.ErrorMessage);
        return result.Value!;
    }

    [HubMethodName(OpStreamConstants.VersioningHubMethods.DeleteName)]
    public async Task DeleteName(string name, bool cascade = false)
    {
        var result = await router.DeleteNameAsync(name, cascade, Context.ConnectionAborted);
        if (!result.Success) throw new HubException(result.ErrorMessage);
    }

    // ─── Branches ────────────────────────────────────────────────────────────

    [HubMethodName(OpStreamConstants.VersioningHubMethods.ListBranches)]
    public async Task<IReadOnlyList<BranchRef>> ListBranches(string name)
    {
        var result = await router.ListBranchesAsync(name, Context.ConnectionAborted);
        if (!result.Success) throw new HubException(result.ErrorMessage);
        return result.Value!;
    }

    [HubMethodName(OpStreamConstants.VersioningHubMethods.ForkBranch)]
    public async Task<BranchRef> ForkBranch(string name, string fromBranchId, string newBranchId, long? atRevision = null)
    {
        var result = await router.ForkBranchAsync(name, fromBranchId, newBranchId, atRevision, Context.ConnectionAborted);
        if (!result.Success) throw new HubException(result.ErrorMessage);
        return result.Value!;
    }

    [HubMethodName(OpStreamConstants.VersioningHubMethods.DeleteBranch)]
    public async Task DeleteBranch(string name, string branchId)
    {
        var result = await router.DeleteBranchAsync(name, branchId, Context.ConnectionAborted);
        if (!result.Success) throw new HubException(result.ErrorMessage);
    }

    // ─── Versions / tags ─────────────────────────────────────────────────────

    [HubMethodName(OpStreamConstants.VersioningHubMethods.CreateVersion)]
    public async Task<VersionRef> CreateVersion(string name, string branchId, string tag)
    {
        var result = await router.CreateVersionAsync(name, branchId, tag, Context.ConnectionAborted);
        if (!result.Success) throw new HubException(result.ErrorMessage);
        return result.Value!;
    }

    [HubMethodName(OpStreamConstants.VersioningHubMethods.ListVersions)]
    public async Task<IReadOnlyList<VersionRef>> ListVersions(string name, string branchId)
    {
        var result = await router.ListVersionsAsync(name, branchId, Context.ConnectionAborted);
        if (!result.Success) throw new HubException(result.ErrorMessage);
        return result.Value!;
    }

    [HubMethodName(OpStreamConstants.VersioningHubMethods.ReadVersionSnapshot)]
    public async Task<DocumentSnapshot?> ReadVersionSnapshot(string name, string branchId, string tag)
    {
        var result = await router.ReadVersionSnapshotAsync(name, branchId, tag, Context.ConnectionAborted);
        if (!result.Success) throw new HubException(result.ErrorMessage);
        return result.Value;
    }

    [HubMethodName(OpStreamConstants.VersioningHubMethods.DeleteVersion)]
    public async Task DeleteVersion(string name, string branchId, string tag, bool dropSnapshot = false)
    {
        var result = await router.DeleteVersionAsync(name, branchId, tag, dropSnapshot, Context.ConnectionAborted);
        if (!result.Success) throw new HubException(result.ErrorMessage);
    }

    // ─── Merge ───────────────────────────────────────────────────────────────

    [HubMethodName(OpStreamConstants.VersioningHubMethods.MergeBranch)]
    public async Task<MergeReport> MergeBranch(string name, string targetBranchId, string sourceBranchId)
    {
        var result = await router.MergeAsync(
            name, targetBranchId, sourceBranchId,
            TransformPriority.ExistingWins, dryRun: false,
            Context.ConnectionAborted);
        if (!result.Success) throw new HubException(result.ErrorMessage);
        return result.Value!;
    }

    [HubMethodName(OpStreamConstants.VersioningHubMethods.DryRunMerge)]
    public async Task<MergeReport> DryRunMerge(string name, string targetBranchId, string sourceBranchId)
    {
        var result = await router.MergeAsync(
            name, targetBranchId, sourceBranchId,
            TransformPriority.ExistingWins, dryRun: true,
            Context.ConnectionAborted);
        if (!result.Success) throw new HubException(result.ErrorMessage);
        return result.Value!;
    }
}
