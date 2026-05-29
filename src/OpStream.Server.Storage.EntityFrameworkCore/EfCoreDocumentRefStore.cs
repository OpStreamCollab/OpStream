using Microsoft.EntityFrameworkCore;
using OpStream.Server.Versioning;
using System.Runtime.CompilerServices;

namespace OpStream.Server.Storage.EntityFrameworkCore;

/// <summary>
/// EF Core implementation of <see cref="IDocumentRefStore"/>.
/// Works over any EF Core provider (SQL Server, PostgreSQL, SQLite, MySQL…) via <see cref="OpStreamDbContext"/>.
/// </summary>
public class EfCoreDocumentRefStore<TContext> : IDocumentRefStore
    where TContext : OpStreamDbContext
{
    private readonly IDbContextFactory<TContext> _contextFactory;

    public EfCoreDocumentRefStore(IDbContextFactory<TContext> contextFactory)
        => _contextFactory = contextFactory;

    // ─── Names ───────────────────────────────────────────────────────────────

    public async Task<DocumentNameInfo?> GetNameAsync(string globalName, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        var e = await db.DocumentNames.FindAsync(new object[] { globalName }, ct);
        return e is null ? null : ToModel(e);
    }

    public async IAsyncEnumerable<DocumentNameInfo> EnumerateNamesAsync(
        string tenantPrefix,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        var entities = await db.DocumentNames
            .AsNoTracking()
            .Where(e => e.GlobalName.StartsWith(tenantPrefix))
            .ToListAsync(ct);
        foreach (var e in entities)
            yield return ToModel(e);
    }

    public async Task CreateNameAsync(DocumentNameInfo nameInfo, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        db.DocumentNames.Add(new DocumentNameEntity
        {
            GlobalName      = nameInfo.Name,
            DefaultBranchId = nameInfo.DefaultBranchId,
            EngineType      = nameInfo.EngineType,
            CreatedAt       = nameInfo.CreatedAt
        });
        await db.SaveChangesAsync(ct);
    }

    // ─── Branches ────────────────────────────────────────────────────────────

    public async Task<BranchRef?> GetBranchAsync(string globalName, string branchId, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        var e = await db.DocumentBranches.FindAsync(new object[] { globalName, branchId }, ct);
        return e is null ? null : ToModel(e);
    }

    public async IAsyncEnumerable<BranchRef> EnumerateBranchesAsync(
        string globalName,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        var entities = await db.DocumentBranches
            .AsNoTracking()
            .Where(e => e.GlobalName == globalName)
            .ToListAsync(ct);
        foreach (var e in entities)
            yield return ToModel(e);
    }

    public async Task CreateBranchAsync(BranchRef branch, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        db.DocumentBranches.Add(new DocumentBranchEntity
        {
            GlobalName           = branch.Name,
            BranchId             = branch.BranchId,
            PhysicalDocumentId   = branch.PhysicalDocumentId,
            ForkParentBranchId   = branch.ForkParentBranchId,
            ForkRevision         = branch.ForkRevision,
            CreatedAt            = branch.CreatedAt,
            IsReadOnly           = branch.IsReadOnly
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteBranchAsync(string globalName, string branchId, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        var e = await db.DocumentBranches.FindAsync(new object[] { globalName, branchId }, ct);
        if (e is null) return;
        db.DocumentBranches.Remove(e);
        await db.SaveChangesAsync(ct);
    }

    // ─── Versions / tags ─────────────────────────────────────────────────────

    public async Task CreateVersionAsync(VersionRef version, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        db.DocumentVersions.Add(new DocumentVersionEntity
        {
            GlobalName           = version.Name,
            BranchId             = version.BranchId,
            Tag                  = version.Tag,
            Revision             = version.Revision,
            HistorySnapshotName  = version.HistorySnapshotName,
            CreatedAt            = version.CreatedAt
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<VersionRef?> GetVersionAsync(string globalName, string branchId, string tag, CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        var e = await db.DocumentVersions.FindAsync(new object[] { globalName, branchId, tag }, ct);
        return e is null ? null : ToModel(e);
    }

    public async IAsyncEnumerable<VersionRef> EnumerateVersionsAsync(
        string globalName,
        string branchId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);
        var entities = await db.DocumentVersions
            .AsNoTracking()
            .Where(e => e.GlobalName == globalName && e.BranchId == branchId)
            .ToListAsync(ct);
        foreach (var e in entities)
            yield return ToModel(e);
    }

    // ─── Compaction pin guard ─────────────────────────────────────────────────

    public async IAsyncEnumerable<VersionRef> GetPinnedVersionsForDocumentAsync(
        string physicalDocumentId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct);

        // Join branches → versions to find all version refs whose branch's physicalDocumentId matches.
        var entities = await (
            from b in db.DocumentBranches.AsNoTracking()
            join v in db.DocumentVersions.AsNoTracking()
                on new { b.GlobalName, b.BranchId } equals new { v.GlobalName, v.BranchId }
            where b.PhysicalDocumentId == physicalDocumentId
            select v
        ).ToListAsync(ct);

        foreach (var e in entities)
            yield return ToModel(e);
    }

    // ─── Mapping ─────────────────────────────────────────────────────────────

    private static DocumentNameInfo ToModel(DocumentNameEntity e) =>
        new(e.GlobalName, e.DefaultBranchId, e.EngineType, e.CreatedAt);

    private static BranchRef ToModel(DocumentBranchEntity e) =>
        new(e.GlobalName, e.BranchId, e.PhysicalDocumentId,
            e.ForkParentBranchId, e.ForkRevision, e.CreatedAt, e.IsReadOnly);

    private static VersionRef ToModel(DocumentVersionEntity e) =>
        new(e.GlobalName, e.BranchId, e.Tag, e.Revision, e.HistorySnapshotName, e.CreatedAt);
}
