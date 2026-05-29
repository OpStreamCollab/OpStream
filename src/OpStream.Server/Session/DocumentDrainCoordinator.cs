using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpStream.Constants;
using OpStream.Server.Storage;
using OpStream.Shared.Abstractions;
using System.Text.Json;

namespace OpStream.Server.Session;

/// <summary>
/// Coordinates what happens when a document drains (its last peer leaves): notifying host
/// <see cref="IDocumentDrainHandler"/>s with the final state, and — if asked — deleting all of
/// the document's data and broadcasting a cluster-wide eviction.
/// </summary>
public interface IDocumentDrainCoordinator
{
    /// <summary>
    /// Invokes every registered <see cref="IDocumentDrainHandler"/> with the document's final
    /// state and returns the aggregate decision (<see cref="DocumentDrainDecision.Delete"/> if any
    /// handler asked for deletion).
    /// </summary>
    Task<DocumentDrainDecision> NotifyAsync(IDocumentSession session, CancellationToken ct = default);

    /// <summary>
    /// Permanently deletes a document's data — current state, op log, snapshots and history —
    /// then broadcasts a cluster-wide eviction and releases ownership. The caller is responsible
    /// for closing the local session first.
    /// </summary>
    Task DeleteDataAsync(string documentId, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class DocumentDrainCoordinator(
    IServiceScopeFactory scopeFactory,
    IBackplane backplane,
    IDocumentOwnershipManager ownershipManager,
    ILogger<DocumentDrainCoordinator> logger) : IDocumentDrainCoordinator
{
    /// <inheritdoc />
    public async Task<DocumentDrainDecision> NotifyAsync(IDocumentSession session, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var handlers = scope.ServiceProvider.GetServices<IDocumentDrainHandler>().ToArray();
        if (handlers.Length == 0)
            return DocumentDrainDecision.Keep;

        DocumentDrainContext drainContext;
        try
        {
            drainContext = new DocumentDrainContext(
                session.DocumentId,
                session.DocumentType,
                session.CurrentRevision,
                session.SerializeState(),
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to capture final state for drained document {DocId}", session.DocumentId);
            return DocumentDrainDecision.Keep;
        }

        var decision = DocumentDrainDecision.Keep;
        foreach (var handler in handlers)
        {
            try
            {
                var result = await handler.OnDocumentDrainedAsync(drainContext, ct);
                if (result == DocumentDrainDecision.Delete)
                    decision = DocumentDrainDecision.Delete; // delete wins; still run the rest
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Document drain handler {Handler} failed for document {DocId}",
                    handler.GetType().Name, session.DocumentId);
            }
        }

        return decision;
    }

    /// <inheritdoc />
    public async Task DeleteDataAsync(string documentId, CancellationToken ct = default)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            var history = scope.ServiceProvider.GetRequiredService<IHistoryStore>();

            await store.DeleteAsync(documentId, ct);
            try { await history.DeleteAsync(documentId, ct); }
            catch (NotSupportedException) { /* history backend may not support delete */ }

            // Tell every other node to drop any cached state for this document.
            await backplane.PublishAsync(
                OpStreamConstants.ManagementChannels.ClusterBroadcast,
                new BackplaneMessage(
                    backplane.NodeId,
                    OpStreamConstants.BackplaneMessages.DocumentDeleted,
                    JsonSerializer.SerializeToUtf8Bytes(documentId, OpStreamJsonOptions.Default)),
                ct);

            try { await ownershipManager.ReleaseOwnershipAsync(documentId, backplane.NodeId, ct); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to release ownership for deleted drained document {DocId}", documentId);
            }

            logger.LogInformation("Deleted drained document {DocId} at host request", documentId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete drained document {DocId}", documentId);
        }
    }
}
