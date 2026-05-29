namespace OpStream.Server.Versioning;

/// <summary>
/// Persistence contract for the versioning ref registry: names, branches, and version tags.
/// Operates on global (tenant-scoped) names; multi-tenancy is resolved by the caller.
/// Default implementations throw <see cref="NotSupportedException"/> so providers that have
/// not yet implemented versioning continue to compile — versioning endpoints simply stay disabled.
/// </summary>
public interface IDocumentRefStore
{
    // ─── Names ───────────────────────────────────────────────────────────────

    Task<DocumentNameInfo?> GetNameAsync(string globalName, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement {nameof(GetNameAsync)}.");

    IAsyncEnumerable<DocumentNameInfo> EnumerateNamesAsync(string tenantPrefix, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement {nameof(EnumerateNamesAsync)}.");

    Task CreateNameAsync(DocumentNameInfo nameInfo, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement {nameof(CreateNameAsync)}.");

    // ─── Branches ────────────────────────────────────────────────────────────

    Task<BranchRef?> GetBranchAsync(string globalName, string branchId, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement {nameof(GetBranchAsync)}.");

    IAsyncEnumerable<BranchRef> EnumerateBranchesAsync(string globalName, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement {nameof(EnumerateBranchesAsync)}.");

    Task CreateBranchAsync(BranchRef branch, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement {nameof(CreateBranchAsync)}.");

    Task DeleteBranchAsync(string globalName, string branchId, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement {nameof(DeleteBranchAsync)}.");

    // ─── Versions / tags (immutable) ─────────────────────────────────────────

    Task CreateVersionAsync(VersionRef version, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement {nameof(CreateVersionAsync)}.");

    Task<VersionRef?> GetVersionAsync(string globalName, string branchId, string tag, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement {nameof(GetVersionAsync)}.");

    IAsyncEnumerable<VersionRef> EnumerateVersionsAsync(string globalName, string branchId, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement {nameof(EnumerateVersionsAsync)}.");

    // ─── Compaction pin guard ─────────────────────────────────────────────────

    /// <summary>
    /// Returns all version refs whose backing history snapshot lives in the given physical document.
    /// Used by the compaction guard to determine the oldest revision that must not be purged.
    /// </summary>
    IAsyncEnumerable<VersionRef> GetPinnedVersionsForDocumentAsync(string physicalDocumentId, CancellationToken ct = default)
        => throw new NotSupportedException($"{GetType().Name} does not implement {nameof(GetPinnedVersionsForDocumentAsync)}.");
}
