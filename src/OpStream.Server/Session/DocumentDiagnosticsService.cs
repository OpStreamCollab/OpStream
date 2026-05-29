using Microsoft.Extensions.DependencyInjection;
using OpStream.Server.Diagnostics;
using OpStream.Server.Storage;
using OpStream.Shared.Abstractions;

namespace OpStream.Server.Session;

/// <summary>
/// Builds read-only diagnostic snapshots of a document, combining the live session view (peers,
/// revision) with the on-disk op-log tail. Pure read model — no mutable state.
/// </summary>
public interface IDocumentDiagnosticsService
{
    Task<DocumentDiagnostics> GetSnapshotAsync(string documentId, int recentOpCount = 50, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class DocumentDiagnosticsService(
    IDocumentSessionRegistry sessions,
    IServiceScopeFactory scopeFactory,
    IBackplane backplane,
    IDocumentOwnershipManager ownershipManager) : IDocumentDiagnosticsService
{
    /// <inheritdoc />
    public async Task<DocumentDiagnostics> GetSnapshotAsync(
        string documentId, int recentOpCount = 50, CancellationToken ct = default)
    {
        string? ownerNodeId = null;
        try
        {
            ownerNodeId = await ownershipManager.GetOrAcquireOwnerAsync(documentId, backplane.NodeId, ct);
        }
        catch
        {
            // Ownership lookup is best-effort for diagnostics.
        }

        long revision = 0;
        IReadOnlyList<string> peers = Array.Empty<string>();
        var session = sessions.TryGet(documentId);
        bool activeHere = session is not null;
        if (session is not null)
        {
            revision = session.CurrentRevision;
            peers = session.Peers.ToArray();
        }

        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

        long from = Math.Max(0, revision - recentOpCount);
        var recentOps = new List<DiagnosticsOpEntry>(recentOpCount);
        await foreach (var op in store.StreamOpsAsync(documentId, from, ct))
        {
            recentOps.Add(new DiagnosticsOpEntry(op.Revision, op.AuthorId, op.Timestamp, op.Payload.Length, op.EngineType));
            if (recentOps.Count >= recentOpCount) break;
        }
        if (revision == 0 && recentOps.Count > 0)
            revision = recentOps[^1].Revision;

        return new DocumentDiagnostics(
            DocumentId: documentId,
            ActiveOnThisNode: activeHere,
            OwnerNodeId: ownerNodeId,
            Revision: revision,
            PeerCount: peers.Count,
            Peers: peers,
            RecentOps: recentOps);
    }
}
