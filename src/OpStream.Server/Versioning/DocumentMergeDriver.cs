using Microsoft.Extensions.DependencyInjection;
using OpStream.Server.Engine;
using OpStream.Server.Models;
using OpStream.Server.Storage;
using System.Text.Json;

namespace OpStream.Server.Versioning;

/// <summary>
/// 3-way merge driver for a specific engine type.
/// Reads both branches' ops from <see cref="IDocumentStore"/> and <see cref="IHistoryStore"/>,
/// transforms the source ops against the target's concurrent ops via <see cref="IOpEngine{TDoc,TOp}"/>,
/// and appends the rebased ops to the target's op log.
/// </summary>
public class DocumentMergeDriver<TDoc, TOp>(
    string engineType,
    IOpEngine<TDoc, TOp> engine,
    IServiceScopeFactory scopeFactory) : IDocumentMergeDriver
{
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string EngineType => engineType;

    public async Task<MergeReport> MergeAsync(
        string targetPhysicalDocumentId,
        string targetBranchId,
        string sourcePhysicalDocumentId,
        string sourceBranchId,
        TransformPriority priority = TransformPriority.ExistingWins,
        bool dryRun = false,
        CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var store   = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var history = scope.ServiceProvider.GetRequiredService<IHistoryStore>();

        // 1. Collect target (A) ops — from the hot store then history, since genesis.
        var opsA = await CollectOpsAsync<TOp>(store, history, targetPhysicalDocumentId, ct);

        // 2. Collect source (B) stored ops for stamping purposes.
        var storedOpsB = await CollectStoredOpsAsync(store, history, sourcePhysicalDocumentId, ct);

        // 3. Transform B's ops against A's concurrent ops.
        var rebased   = new List<TOp>();
        int nullified = 0;

        foreach (var storedB in storedOpsB)
        {
            var opB = TryDeserialize(storedB.Payload.Span);
            if (opB is null) continue;

            TOp? current = opB;
            var nullified_inner = false;
            foreach (var opA in opsA)
            {
                current = engine.Transform(current!, opA, priority);
                if (current is null || engine.IsNoOp(current))
                {
                    current = default;
                    nullified_inner = true;
                    break;
                }
            }

            if (!nullified_inner && current is not null && !engine.IsNoOp(current))
                rebased.Add(current);
            else
                nullified++;
        }

        // 4. Append rebased ops to the target when not a dry run.
        if (!dryRun && rebased.Count > 0)
        {
            var targetInfo = await store.GetInfoAsync(targetPhysicalDocumentId, ct);
            long nextRevision = (targetInfo?.Revision ?? 0) + 1;

            foreach (var op in rebased)
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(op, _json);
                var stored  = new StoredOp(nextRevision++, "merge-system", DateTimeOffset.UtcNow,
                    payload, engineType);
                await store.AppendOpAsync(targetPhysicalDocumentId, stored, ct);
                await history.AppendHistoryOpAsync(targetPhysicalDocumentId, stored, ct);
            }

            // Write a named merge milestone for audit / lineage.
            var mergeSnapshot = await store.LoadSnapshotAsync(targetPhysicalDocumentId, ct);
            if (mergeSnapshot is not null)
            {
                await history.WriteHistorySnapshotAsync(
                    targetPhysicalDocumentId,
                    mergeSnapshot,
                    $"merge/{sourceBranchId}",
                    ct);
            }
        }

        return new MergeReport(sourceBranchId, targetBranchId, rebased.Count, nullified, dryRun);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async Task<List<TOp>> CollectOpsAsync<T>(
        IDocumentStore store, IHistoryStore history, string physId, CancellationToken ct)
    {
        var ops = new List<TOp>();
        // Hot store (since last snapshot)
        var snapshot = await store.LoadSnapshotAsync(physId, ct);
        long since = snapshot?.Revision ?? 0;
        await foreach (var s in store.StreamOpsAsync(physId, since, ct))
        {
            var op = TryDeserialize(s.Payload.Span);
            if (op is not null) ops.Add(op);
        }
        // History (everything up to snapshot revision)
        if (since > 0)
        {
            await foreach (var s in history.StreamHistoryOpsAsync(physId, 0, since, ct))
            {
                var op = TryDeserialize(s.Payload.Span);
                if (op is not null) ops.Add(op);
            }
        }
        return ops;
    }

    private async Task<List<StoredOp>> CollectStoredOpsAsync(
        IDocumentStore store, IHistoryStore history, string physId, CancellationToken ct)
    {
        var all = new List<StoredOp>();
        var snapshot = await store.LoadSnapshotAsync(physId, ct);
        long since = snapshot?.Revision ?? 0;

        // History first (oldest → newest)
        if (since > 0)
        {
            await foreach (var s in history.StreamHistoryOpsAsync(physId, 0, since, ct))
                all.Add(s);
        }
        // Hot store
        await foreach (var s in store.StreamOpsAsync(physId, since, ct))
            all.Add(s);

        return all;
    }

    private TOp? TryDeserialize(ReadOnlySpan<byte> payload)
    {
        try { return JsonSerializer.Deserialize<TOp>(payload, _json); }
        catch { return default; }
    }
}
