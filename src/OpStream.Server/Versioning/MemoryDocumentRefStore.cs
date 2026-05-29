using OpStream.Shared.Messages;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace OpStream.Server.Versioning;

/// <summary>
/// In-memory <see cref="IDocumentRefStore"/> for tests and development.
/// Not for production — data is lost when the process restarts.
/// </summary>
public class MemoryDocumentRefStore : IDocumentRefStore
{
    private readonly ConcurrentDictionary<string, DocumentNameInfo> _names  = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BranchRef>        _branches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, VersionRef>       _versions = new(StringComparer.Ordinal);

    // ─── Names ───────────────────────────────────────────────────────────────

    public Task<DocumentNameInfo?> GetNameAsync(string globalName, CancellationToken ct = default)
    {
        _names.TryGetValue(globalName, out var info);
        return Task.FromResult(info);
    }

    public async IAsyncEnumerable<DocumentNameInfo> EnumerateNamesAsync(
        string tenantPrefix,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var (key, value) in _names)
        {
            ct.ThrowIfCancellationRequested();
            if (key.StartsWith(tenantPrefix, StringComparison.Ordinal))
                yield return value;
        }
        await Task.CompletedTask;
    }

    public Task CreateNameAsync(DocumentNameInfo nameInfo, CancellationToken ct = default)
    {
        _names[nameInfo.Name] = nameInfo;
        return Task.CompletedTask;
    }

    // ─── Branches ────────────────────────────────────────────────────────────

    public Task<BranchRef?> GetBranchAsync(string globalName, string branchId, CancellationToken ct = default)
    {
        _branches.TryGetValue(BranchKey(globalName, branchId), out var branch);
        return Task.FromResult(branch);
    }

    public async IAsyncEnumerable<BranchRef> EnumerateBranchesAsync(
        string globalName,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var prefix = globalName + "\x00";
        foreach (var (key, value) in _branches)
        {
            ct.ThrowIfCancellationRequested();
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                yield return value;
        }
        await Task.CompletedTask;
    }

    public Task CreateBranchAsync(BranchRef branch, CancellationToken ct = default)
    {
        _branches[BranchKey(branch.Name, branch.BranchId)] = branch;
        return Task.CompletedTask;
    }

    public Task DeleteBranchAsync(string globalName, string branchId, CancellationToken ct = default)
    {
        _branches.TryRemove(BranchKey(globalName, branchId), out _);
        return Task.CompletedTask;
    }

    // ─── Versions / tags ─────────────────────────────────────────────────────

    public Task CreateVersionAsync(VersionRef version, CancellationToken ct = default)
    {
        _versions[VersionKey(version.Name, version.BranchId, version.Tag)] = version;
        return Task.CompletedTask;
    }

    public Task<VersionRef?> GetVersionAsync(string globalName, string branchId, string tag, CancellationToken ct = default)
    {
        _versions.TryGetValue(VersionKey(globalName, branchId, tag), out var version);
        return Task.FromResult(version);
    }

    public async IAsyncEnumerable<VersionRef> EnumerateVersionsAsync(
        string globalName,
        string branchId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var prefix = BranchKey(globalName, branchId) + "\x00";
        foreach (var (key, value) in _versions)
        {
            ct.ThrowIfCancellationRequested();
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                yield return value;
        }
        await Task.CompletedTask;
    }

    // ─── Compaction pin guard ─────────────────────────────────────────────────

    public async IAsyncEnumerable<VersionRef> GetPinnedVersionsForDocumentAsync(
        string physicalDocumentId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var version in _versions.Values)
        {
            ct.ThrowIfCancellationRequested();
            var branchKey = BranchKey(version.Name, version.BranchId);
            if (_branches.TryGetValue(branchKey, out var branch) &&
                string.Equals(branch.PhysicalDocumentId, physicalDocumentId, StringComparison.Ordinal))
            {
                yield return version;
            }
        }
        await Task.CompletedTask;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    // Use null byte as separator so names/branchIds that contain any printable chars can't collide.
    private static string BranchKey(string name, string branchId)  => $"{name}\x00{branchId}";
    private static string VersionKey(string name, string branchId, string tag) => $"{name}\x00{branchId}\x00{tag}";
}
