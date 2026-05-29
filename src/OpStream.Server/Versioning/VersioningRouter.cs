using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpStream.Server.Multitenancy;
using OpStream.Server.Storage;
using OpStream.Shared.Abstractions;
using OpStream.Shared.Messages;

namespace OpStream.Server.Versioning;

/// <summary>
/// Control-plane router for naming, branching, tagging, and merging.
/// Follows the same conventions as <c>DatabaseCommandRouter</c>:
/// <list type="bullet">
///   <item>Every public method calls <see cref="AuthorizeAsync"/> first.</item>
///   <item>Receives <em>local</em> names from the transport.</item>
///   <item>Globalizes them via <see cref="IDocumentIdGlobalizer"/> before hitting any store.</item>
///   <item>Strips the global prefix before returning to callers.</item>
/// </list>
/// </summary>
public class VersioningRouter(
    IServiceScopeFactory scopeFactory,
    IDocumentRefStore refStore,
    MergeDriverRegistry mergeDrivers,
    IDocumentIdGlobalizer globalizer,
    ILogger<VersioningRouter> logger)
{
    // ─── Names ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers a human-readable name for an existing physical document and creates its root branch.
    /// </summary>
    public async Task<OpResult<DocumentNameInfo>> RegisterNameAsync(
        string localName,
        string localPhysicalDocumentId,
        string engineType,
        string rootBranchId = "main",
        CancellationToken ct = default)
    {
        if (!await AuthorizeAsync(new DatabaseCommandContext(DatabaseCommandType.RegisterName, localName), ct))
            return Forbidden<DocumentNameInfo>();
        try
        {
            if (!ValidateId(localName, out var e1)) return OpResult<DocumentNameInfo>.Fail(e1);
            if (!ValidateId(rootBranchId, out var e2)) return OpResult<DocumentNameInfo>.Fail(e2);

            var globalName    = globalizer.ToGlobalId(localName);
            var globalPhysId  = globalizer.ToGlobalId(localPhysicalDocumentId);

            var nameInfo = new DocumentNameInfo(globalName, rootBranchId, engineType, DateTimeOffset.UtcNow);
            await refStore.CreateNameAsync(nameInfo, ct);

            var branch = new BranchRef(globalName, rootBranchId, globalPhysId,
                ForkParentBranchId: null, ForkRevision: 0, DateTimeOffset.UtcNow, IsReadOnly: false);
            await refStore.CreateBranchAsync(branch, ct);

            return OpResult<DocumentNameInfo>.Ok(nameInfo with { Name = localName });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "RegisterName failed for {Name}", localName);
            return OpResult<DocumentNameInfo>.Fail(ex.Message);
        }
    }

    public async Task<OpResult<IReadOnlyList<DocumentNameInfo>>> ListNamesAsync(CancellationToken ct = default)
    {
        if (!await AuthorizeAsync(new DatabaseCommandContext(DatabaseCommandType.ListNames, null), ct))
            return Forbidden<IReadOnlyList<DocumentNameInfo>>();
        try
        {
            var prefix  = globalizer.GetCurrentTenantPrefix();
            var results = new List<DocumentNameInfo>();
            await foreach (var info in refStore.EnumerateNamesAsync(prefix, ct))
                results.Add(info with { Name = globalizer.ToLocalId(info.Name) });
            return OpResult<IReadOnlyList<DocumentNameInfo>>.Ok(results);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ListNames failed");
            return OpResult<IReadOnlyList<DocumentNameInfo>>.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Deletes a name and, when <paramref name="cascade"/> is true, all its branches and versions.
    /// When <paramref name="cascade"/> is false the call is refused if any branch still exists.
    /// </summary>
    public async Task<OpResult> DeleteNameAsync(
        string localName, bool cascade = false, CancellationToken ct = default)
    {
        if (!await AuthorizeAsync(new DatabaseCommandContext(
                DatabaseCommandType.DeleteName, localName,
                new Dictionary<string, string> { ["cascade"] = cascade.ToString() }), ct))
            return OpResult.Fail("Forbidden: Insufficient permissions for this operation.");
        try
        {
            var globalName = globalizer.ToGlobalId(localName);

            var branches = new List<BranchRef>();
            await foreach (var b in refStore.EnumerateBranchesAsync(globalName, ct))
                branches.Add(b);

            if (branches.Count > 0 && !cascade)
                return OpResult.Fail(
                    $"Cannot delete name '{localName}': {branches.Count} branch(es) still exist. " +
                    "Delete them first or pass cascade=true.");

            if (cascade)
            {
                // Delete leaf branches first (no children), repeat until empty.
                var remaining = branches.ToList();
                int maxPasses = remaining.Count + 1;
                for (int pass = 0; pass < maxPasses && remaining.Count > 0; pass++)
                {
                    var deletedAny = false;
                    for (int i = remaining.Count - 1; i >= 0; i--)
                    {
                        var b = remaining[i];
                        bool hasChild = remaining.Any(x =>
                            string.Equals(x.ForkParentBranchId, b.BranchId, StringComparison.Ordinal));
                        if (hasChild) continue;

                        var del = await DeleteBranchAsync(localName, b.BranchId, ct);
                        if (!del.Success) return del;
                        remaining.RemoveAt(i);
                        deletedAny = true;
                    }
                    if (!deletedAny) break;
                }
            }

            await refStore.DeleteNameAsync(globalName, ct);
            return OpResult.Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DeleteName failed for {Name}", localName);
            return OpResult.Fail(ex.Message);
        }
    }

    // ─── Branches ────────────────────────────────────────────────────────────

    public async Task<OpResult<IReadOnlyList<BranchRef>>> ListBranchesAsync(
        string localName, CancellationToken ct = default)
    {
        if (!await AuthorizeAsync(new DatabaseCommandContext(DatabaseCommandType.ListBranches, localName), ct))
            return Forbidden<IReadOnlyList<BranchRef>>();
        try
        {
            var globalName = globalizer.ToGlobalId(localName);
            var results    = new List<BranchRef>();
            await foreach (var b in refStore.EnumerateBranchesAsync(globalName, ct))
                results.Add(LocaliseBranch(b));
            return OpResult<IReadOnlyList<BranchRef>>.Ok(results);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ListBranches failed for {Name}", localName);
            return OpResult<IReadOnlyList<BranchRef>>.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Forks <paramref name="fromBranchId"/> into a new independent branch.
    /// When <paramref name="atRevision"/> is null, the fork point is the latest available snapshot
    /// in the hot store. Specify a revision to fork from a historical snapshot in the history store.
    /// </summary>
    public async Task<OpResult<BranchRef>> ForkBranchAsync(
        string localName,
        string fromBranchId,
        string newBranchId,
        long? atRevision = null,
        CancellationToken ct = default)
    {
        if (!await AuthorizeAsync(new DatabaseCommandContext(
                DatabaseCommandType.ForkBranch, localName,
                new Dictionary<string, string> { ["fromBranchId"] = fromBranchId, ["newBranchId"] = newBranchId }), ct))
            return Forbidden<BranchRef>();
        try
        {
            if (!ValidateId(newBranchId, out var err)) return OpResult<BranchRef>.Fail(err);

            var globalName = globalizer.ToGlobalId(localName);
            var fromBranch = await refStore.GetBranchAsync(globalName, fromBranchId, ct);
            if (fromBranch is null)  return OpResult<BranchRef>.Fail($"Branch '{fromBranchId}' not found.");
            if (fromBranch.IsReadOnly) return OpResult<BranchRef>.Fail($"Branch '{fromBranchId}' is read-only.");

            var existing = await refStore.GetBranchAsync(globalName, newBranchId, ct);
            if (existing is not null) return OpResult<BranchRef>.Fail($"Branch '{newBranchId}' already exists.");

            using var scope   = scopeFactory.CreateScope();
            var store         = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            var history       = scope.ServiceProvider.GetRequiredService<IHistoryStore>();

            DocumentSnapshot forkSnapshot;
            long forkRevision;

            if (atRevision.HasValue)
            {
                var snap = await history.LoadHistorySnapshotAsync(
                    fromBranch.PhysicalDocumentId, atRevision.Value, ct);
                if (snap is null)
                    return OpResult<BranchRef>.Fail(
                        $"No history snapshot found at or before revision {atRevision.Value}.");
                forkSnapshot  = snap;
                forkRevision  = snap.Revision;
            }
            else
            {
                var snap = await store.LoadSnapshotAsync(fromBranch.PhysicalDocumentId, ct);
                if (snap is null)
                    return OpResult<BranchRef>.Fail(
                        "Source branch has no snapshot yet — compact the document first.");
                forkSnapshot = snap;
                forkRevision = snap.Revision;
            }

            var newGlobalPhysId = globalizer.ToGlobalId($"{localName}@{newBranchId}");

            await store.WriteSnapshotAsync(
                newGlobalPhysId,
                forkSnapshot with { Revision = 0, Timestamp = DateTimeOffset.UtcNow },
                ct);

            var newBranch = new BranchRef(
                globalName, newBranchId, newGlobalPhysId,
                fromBranchId, forkRevision,
                DateTimeOffset.UtcNow, IsReadOnly: false);
            await refStore.CreateBranchAsync(newBranch, ct);

            return OpResult<BranchRef>.Ok(LocaliseBranch(newBranch));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ForkBranch failed {From}→{New} for {Name}", fromBranchId, newBranchId, localName);
            return OpResult<BranchRef>.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Deletes a branch and its physical op log. Refuses if the branch has live children.
    /// </summary>
    public async Task<OpResult> DeleteBranchAsync(
        string localName, string branchId, CancellationToken ct = default)
    {
        if (!await AuthorizeAsync(new DatabaseCommandContext(
                DatabaseCommandType.DeleteBranch, localName,
                new Dictionary<string, string> { ["branchId"] = branchId }), ct))
            return OpResult.Fail("Forbidden: Insufficient permissions for this operation.");
        try
        {
            var globalName = globalizer.ToGlobalId(localName);

            await foreach (var child in refStore.EnumerateBranchesAsync(globalName, ct))
            {
                if (string.Equals(child.ForkParentBranchId, branchId, StringComparison.Ordinal))
                    return OpResult.Fail(
                        $"Cannot delete branch '{branchId}': branch '{child.BranchId}' was forked from it.");
            }

            var branch = await refStore.GetBranchAsync(globalName, branchId, ct);
            if (branch is null) return OpResult.Ok();

            using var scope = scopeFactory.CreateScope();
            var store       = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            var history     = scope.ServiceProvider.GetRequiredService<IHistoryStore>();

            await store.DeleteAsync(branch.PhysicalDocumentId, ct);
            try { await history.DeleteAsync(branch.PhysicalDocumentId, ct); }
            catch (NotSupportedException) { }

            await refStore.DeleteBranchAsync(globalName, branchId, ct);
            return OpResult.Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DeleteBranch failed {Branch} for {Name}", branchId, localName);
            return OpResult.Fail(ex.Message);
        }
    }

    // ─── Versions / tags ─────────────────────────────────────────────────────

    /// <summary>
    /// Creates an immutable tag pointing at the current revision of the branch.
    /// Writes a named history snapshot so the bytes survive future compaction.
    /// </summary>
    public async Task<OpResult<VersionRef>> CreateVersionAsync(
        string localName, string branchId, string tag, CancellationToken ct = default)
    {
        if (!await AuthorizeAsync(new DatabaseCommandContext(
                DatabaseCommandType.CreateVersion, localName,
                new Dictionary<string, string> { ["branchId"] = branchId, ["tag"] = tag }), ct))
            return Forbidden<VersionRef>();
        try
        {
            if (!ValidateId(tag, out var err)) return OpResult<VersionRef>.Fail(err);

            var globalName = globalizer.ToGlobalId(localName);
            var branch     = await refStore.GetBranchAsync(globalName, branchId, ct);
            if (branch is null) return OpResult<VersionRef>.Fail($"Branch '{branchId}' not found.");

            var dupe = await refStore.GetVersionAsync(globalName, branchId, tag, ct);
            if (dupe is not null) return OpResult<VersionRef>.Fail($"Tag '{tag}' already exists on branch '{branchId}'.");

            using var scope = scopeFactory.CreateScope();
            var store       = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            var history     = scope.ServiceProvider.GetRequiredService<IHistoryStore>();

            var info = await store.GetInfoAsync(branch.PhysicalDocumentId, ct);
            if (info is null) return OpResult<VersionRef>.Fail("Document has no data.");

            var snapshot = await store.LoadSnapshotAsync(branch.PhysicalDocumentId, ct);
            if (snapshot is null)
                return OpResult<VersionRef>.Fail("Document has no snapshot — compact the document first.");

            var milestoneName = $"tag/{tag}";
            await history.WriteHistorySnapshotAsync(
                branch.PhysicalDocumentId,
                snapshot with { Revision = info.Revision },
                milestoneName,
                ct);

            var version = new VersionRef(globalName, branchId, tag, info.Revision, milestoneName, DateTimeOffset.UtcNow);
            await refStore.CreateVersionAsync(version, ct);
            return OpResult<VersionRef>.Ok(version with { Name = localName });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "CreateVersion failed {Tag} on {Branch} for {Name}", tag, branchId, localName);
            return OpResult<VersionRef>.Fail(ex.Message);
        }
    }

    public async Task<OpResult<IReadOnlyList<VersionRef>>> ListVersionsAsync(
        string localName, string branchId, CancellationToken ct = default)
    {
        if (!await AuthorizeAsync(new DatabaseCommandContext(
                DatabaseCommandType.ListVersions, localName,
                new Dictionary<string, string> { ["branchId"] = branchId }), ct))
            return Forbidden<IReadOnlyList<VersionRef>>();
        try
        {
            var globalName = globalizer.ToGlobalId(localName);
            var results    = new List<VersionRef>();
            await foreach (var v in refStore.EnumerateVersionsAsync(globalName, branchId, ct))
                results.Add(v with { Name = localName });
            return OpResult<IReadOnlyList<VersionRef>>.Ok(results);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ListVersions failed {Branch} for {Name}", branchId, localName);
            return OpResult<IReadOnlyList<VersionRef>>.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Returns the raw snapshot bytes for a named version, or null if not found.
    /// </summary>
    public async Task<OpResult<DocumentSnapshot?>> ReadVersionSnapshotAsync(
        string localName, string branchId, string tag, CancellationToken ct = default)
    {
        if (!await AuthorizeAsync(new DatabaseCommandContext(
                DatabaseCommandType.ReadVersionSnapshot, localName,
                new Dictionary<string, string> { ["branchId"] = branchId, ["tag"] = tag }), ct))
            return Forbidden<DocumentSnapshot?>();
        try
        {
            var globalName = globalizer.ToGlobalId(localName);
            var version    = await refStore.GetVersionAsync(globalName, branchId, tag, ct);
            if (version is null) return OpResult<DocumentSnapshot?>.Fail($"Version '{tag}' not found on branch '{branchId}'.");

            var branch = await refStore.GetBranchAsync(globalName, branchId, ct);
            if (branch is null) return OpResult<DocumentSnapshot?>.Fail($"Branch '{branchId}' not found.");

            using var scope = scopeFactory.CreateScope();
            var history     = scope.ServiceProvider.GetRequiredService<IHistoryStore>();
            var snapshot    = await history.LoadHistorySnapshotAsync(branch.PhysicalDocumentId, version.Revision, ct);
            return OpResult<DocumentSnapshot?>.Ok(snapshot);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ReadVersionSnapshot failed {Tag} on {Branch} for {Name}", tag, branchId, localName);
            return OpResult<DocumentSnapshot?>.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Removes a version tag and unpins the compaction floor.
    /// The backing history snapshot is left intact by default; pass <paramref name="dropSnapshot"/>
    /// to also delete it (only if the history store supports it).
    /// </summary>
    public async Task<OpResult> DeleteVersionAsync(
        string localName, string branchId, string tag,
        bool dropSnapshot = false,
        CancellationToken ct = default)
    {
        if (!await AuthorizeAsync(new DatabaseCommandContext(
                DatabaseCommandType.DeleteVersion, localName,
                new Dictionary<string, string> { ["branchId"] = branchId, ["tag"] = tag }), ct))
            return OpResult.Fail("Forbidden: Insufficient permissions for this operation.");
        try
        {
            var globalName = globalizer.ToGlobalId(localName);
            var version    = await refStore.GetVersionAsync(globalName, branchId, tag, ct);
            if (version is null) return OpResult.Ok(); // idempotent

            await refStore.DeleteVersionAsync(globalName, branchId, tag, ct);

            if (dropSnapshot)
            {
                var branch = await refStore.GetBranchAsync(globalName, branchId, ct);
                if (branch is not null)
                {
                    using var scope = scopeFactory.CreateScope();
                    var history     = scope.ServiceProvider.GetRequiredService<IHistoryStore>();
                    try { await history.DeleteMilestoneAsync(branch.PhysicalDocumentId, version.HistorySnapshotName, ct); }
                    catch (NotSupportedException) { }
                }
            }

            return OpResult.Ok();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DeleteVersion failed {Tag} on {Branch} for {Name}", tag, branchId, localName);
            return OpResult.Fail(ex.Message);
        }
    }

    // ─── Merge ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Merges <paramref name="sourceBranchId"/> into <paramref name="targetBranchId"/> using a
    /// 3-way OT/CRDT transform. Pass <paramref name="dryRun"/> = true to preview the report
    /// without committing any ops.
    /// </summary>
    public async Task<OpResult<MergeReport>> MergeAsync(
        string localName,
        string targetBranchId,
        string sourceBranchId,
        TransformPriority priority = TransformPriority.ExistingWins,
        bool dryRun = false,
        CancellationToken ct = default)
    {
        if (!await AuthorizeAsync(new DatabaseCommandContext(
                DatabaseCommandType.MergeBranch, localName,
                new Dictionary<string, string>
                {
                    ["targetBranchId"] = targetBranchId,
                    ["sourceBranchId"] = sourceBranchId,
                    ["dryRun"] = dryRun.ToString()
                }), ct))
            return Forbidden<MergeReport>();
        try
        {
            var globalName  = globalizer.ToGlobalId(localName);
            var nameInfo    = await refStore.GetNameAsync(globalName, ct);
            if (nameInfo is null) return OpResult<MergeReport>.Fail($"Name '{localName}' not registered.");

            var targetBranch = await refStore.GetBranchAsync(globalName, targetBranchId, ct);
            if (targetBranch is null) return OpResult<MergeReport>.Fail($"Target branch '{targetBranchId}' not found.");
            if (targetBranch.IsReadOnly) return OpResult<MergeReport>.Fail($"Target branch '{targetBranchId}' is read-only.");

            var sourceBranch = await refStore.GetBranchAsync(globalName, sourceBranchId, ct);
            if (sourceBranch is null) return OpResult<MergeReport>.Fail($"Source branch '{sourceBranchId}' not found.");

            var driver = mergeDrivers.Get(nameInfo.EngineType);
            if (driver is null)
                return OpResult<MergeReport>.Fail(
                    $"No merge driver registered for engine type '{nameInfo.EngineType}'. " +
                    "Call UseVersioningMerge<TDoc,TOp>(engineType) in your startup configuration.");

            var report = await driver.MergeAsync(
                targetBranch.PhysicalDocumentId, targetBranchId,
                sourceBranch.PhysicalDocumentId, sourceBranchId,
                priority, dryRun, ct);

            return OpResult<MergeReport>.Ok(report);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Merge failed {Source}→{Target} for {Name}", sourceBranchId, targetBranchId, localName);
            return OpResult<MergeReport>.Fail(ex.Message);
        }
    }

    // ─── Compaction pin guard ─────────────────────────────────────────────────

    /// <summary>
    /// Returns the minimum revision pinned by a tag on the given physical document,
    /// or null if no tags exist. Used by compaction to determine the safe purge floor.
    /// </summary>
    public virtual async Task<long?> GetMinPinnedRevisionAsync(string physicalDocumentId, CancellationToken ct = default)
    {
        long? min = null;
        try
        {
            await foreach (var version in refStore.GetPinnedVersionsForDocumentAsync(physicalDocumentId, ct))
            {
                if (min is null || version.Revision < min)
                    min = version.Revision;
            }
        }
        catch (NotSupportedException) { /* provider doesn't support pin guard yet */ }
        return min;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async ValueTask<bool> AuthorizeAsync(DatabaseCommandContext ctx, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var authorizer = scope.ServiceProvider.GetRequiredService<IDatabaseCommandAuthorizer>();
        return await authorizer.AuthorizeAsync(ctx, ct);
    }

    private static OpResult<T> Forbidden<T>() =>
        OpResult<T>.Fail("Forbidden: Insufficient permissions for this operation.");

    private BranchRef LocaliseBranch(BranchRef b) => b with
    {
        Name = globalizer.ToLocalId(b.Name),
        PhysicalDocumentId = globalizer.ToLocalId(b.PhysicalDocumentId)
    };

    private static bool ValidateId(string value, out string error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Identifier cannot be empty.";
            return false;
        }
        if (value.Contains('@') || value.Contains(":#:"))
        {
            error = $"Identifier '{value}' must not contain '@' or ':#:'.";
            return false;
        }
        error = string.Empty;
        return true;
    }
}
