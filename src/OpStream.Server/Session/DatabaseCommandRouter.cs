using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpStream.Constants;
using OpStream.Server.Comments;
using OpStream.Server.Models;
using OpStream.Server.Multitenancy;
using OpStream.Server.Storage;
using OpStream.Shared.Abstractions;
using System.Text.Json;

namespace OpStream.Server.Session;

/// <summary>
/// Routes management commands (list / inspect / delete / compact / purge) through the same
/// authorization + tenant globalization + backplane plumbing used by <see cref="DocumentRouter"/>.
/// <para>
/// Read-only commands resolve locally against <see cref="IDocumentStore"/> / <see cref="IHistoryStore"/>
/// because storage is shared across nodes. Mutating per-document commands are routed to the
/// owner node so any active in-memory session can be evicted before storage is touched.
/// Tenant-wide purge is fanned out to every node via a well-known broadcast channel.
/// </para>
/// </summary>
public class DatabaseCommandRouter(
    IServiceScopeFactory scopeFactory,
    IBackplane backplane,
    IDocumentOwnershipManager ownershipManager,
    IDocumentIdGlobalizer globalizer,
    DocumentRouter documentRouter,
    CompactWithAnchorsService compactWithAnchors,
    ILogger<DatabaseCommandRouter> logger) : IBackplaneRequestExtension
{
    private IAsyncDisposable? _broadcastSubscription;

    /// <summary>
    /// Subscribes to the cluster broadcast channel so this node honours tenant-wide eviction
    /// requests. Idempotent.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_broadcastSubscription is not null) return;
        _broadcastSubscription = await backplane.SubscribeAsync(
            OpStreamConstants.ManagementChannels.ClusterBroadcast,
            HandleBroadcastAsync,
            ct);
    }

    private async ValueTask HandleBroadcastAsync(BackplaneMessage message)
    {
        try
        {
            switch (message.Type)
            {
                case OpStreamConstants.BackplaneMessages.EvictTenant:
                {
                    var prefix = JsonSerializer.Deserialize<string>(message.Payload.Span, OpStreamJsonOptions.Default)!;
                    foreach (var docId in documentRouter.GetActiveDocumentIds())
                    {
                        if (docId.StartsWith(prefix, StringComparison.Ordinal))
                            await documentRouter.EvictSessionAsync(docId);
                    }
                    break;
                }
                case OpStreamConstants.BackplaneMessages.DocumentDeleted:
                {
                    var docId = JsonSerializer.Deserialize<string>(message.Payload.Span, OpStreamJsonOptions.Default)!;
                    await documentRouter.EvictSessionAsync(docId);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling management broadcast {Type}", message.Type);
        }
    }

    // ─── Public API used by transports ───────────────────────────────────────

    public async Task<OpResult<IReadOnlyList<DocumentInfo>>> ListDocumentsAsync(DocumentQuery query, CancellationToken ct = default)
    {
        if (!await AuthorizeAsync(new DatabaseCommandContext(DatabaseCommandType.ListDocuments, null), ct))
            return Forbidden<IReadOnlyList<DocumentInfo>>();

        var prefix = globalizer.GetCurrentTenantPrefix();
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

        var results = new List<DocumentInfo>();
        await foreach (var info in store.EnumerateAsync(prefix, query, ct))
        {
            // Hide the tenant prefix from callers — they only ever see local ids.
            results.Add(info with { DocumentId = globalizer.ToLocalId(info.DocumentId) });
        }
        return OpResult<IReadOnlyList<DocumentInfo>>.Ok(results);
    }

    public async Task<OpResult<DocumentInfo?>> GetDocumentInfoAsync(string localDocumentId, CancellationToken ct = default)
    {
        if (!await AuthorizeAsync(new DatabaseCommandContext(DatabaseCommandType.GetDocumentInfo, localDocumentId), ct))
            return Forbidden<DocumentInfo?>();

        var globalId = globalizer.ToGlobalId(localDocumentId);
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

        var info = await store.GetInfoAsync(globalId, ct);
        return OpResult<DocumentInfo?>.Ok(info is null ? null : info with { DocumentId = localDocumentId });
    }

    public async Task<OpResult<DocumentSnapshot?>> GetSnapshotAsync(string localDocumentId, CancellationToken ct = default)
    {
        if (!await AuthorizeAsync(new DatabaseCommandContext(DatabaseCommandType.GetSnapshot, localDocumentId), ct))
            return Forbidden<DocumentSnapshot?>();

        var globalId = globalizer.ToGlobalId(localDocumentId);
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

        var snapshot = await store.LoadSnapshotAsync(globalId, ct);
        return OpResult<DocumentSnapshot?>.Ok(snapshot);
    }

    public async Task<OpResult<IReadOnlyList<HistoryMilestone>>> ListMilestonesAsync(string localDocumentId, CancellationToken ct = default)
    {
        if (!await AuthorizeAsync(new DatabaseCommandContext(DatabaseCommandType.ListMilestones, localDocumentId), ct))
            return Forbidden<IReadOnlyList<HistoryMilestone>>();

        var globalId = globalizer.ToGlobalId(localDocumentId);
        using var scope = scopeFactory.CreateScope();
        var history = scope.ServiceProvider.GetRequiredService<IHistoryStore>();

        var milestones = (await history.GetMilestonesAsync(globalId, ct)).ToList();
        return OpResult<IReadOnlyList<HistoryMilestone>>.Ok(milestones);
    }

    public Task<OpResult> DeleteDocumentAsync(string localDocumentId, CancellationToken ct = default) =>
        RouteToOwnerAsync(
            DatabaseCommandType.DeleteDocument,
            localDocumentId,
            OpStreamConstants.BackplaneCommands.DeleteDocument,
            args: null,
            ct);

    public Task<OpResult> CompactDocumentAsync(string localDocumentId, long upToRevision, CancellationToken ct = default) =>
        RouteToOwnerAsync(
            DatabaseCommandType.CompactDocument,
            localDocumentId,
            OpStreamConstants.BackplaneCommands.CompactDocument,
            args: new Dictionary<string, string> { ["upToRevision"] = upToRevision.ToString() },
            ct);

    public Task<OpResult> PurgeHistoryAsync(string localDocumentId, long upToRevision, CancellationToken ct = default) =>
        RouteToOwnerAsync(
            DatabaseCommandType.PurgeHistory,
            localDocumentId,
            OpStreamConstants.BackplaneCommands.PurgeHistory,
            args: new Dictionary<string, string> { ["upToRevision"] = upToRevision.ToString() },
            ct);

    public async Task<OpResult<int>> PurgeTenantAsync(CancellationToken ct = default)
    {
        if (!await AuthorizeAsync(new DatabaseCommandContext(DatabaseCommandType.PurgeTenant, null), ct))
            return Forbidden<int>();

        var prefix = globalizer.GetCurrentTenantPrefix();

        // 1. Fan out eviction so every node closes any active session under this tenant.
        await backplane.PublishAsync(
            OpStreamConstants.ManagementChannels.ClusterBroadcast,
            new BackplaneMessage(
                backplane.NodeId,
                OpStreamConstants.BackplaneMessages.EvictTenant,
                JsonSerializer.SerializeToUtf8Bytes(prefix, OpStreamJsonOptions.Default)),
            ct);

        // 2. Wipe storage on the shared backend.
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var history = scope.ServiceProvider.GetRequiredService<IHistoryStore>();

        var docsDeleted = await store.DeleteByTenantPrefixAsync(prefix, ct);
        try { await history.DeleteByTenantPrefixAsync(prefix, ct); }
        catch (NotSupportedException) { /* history backend may not support bulk delete yet */ }

        return OpResult<int>.Ok(docsDeleted);
    }

    // ─── Owner routing ───────────────────────────────────────────────────────

    private async Task<OpResult> RouteToOwnerAsync(
        DatabaseCommandType command,
        string localDocumentId,
        string backplaneCommand,
        IReadOnlyDictionary<string, string>? args,
        CancellationToken ct)
    {
        try
        {
            if (!await AuthorizeAsync(new DatabaseCommandContext(command, localDocumentId, args), ct))
                return OpResult.Fail("Forbidden: Insufficient permissions for this operation.");

            var globalId = globalizer.ToGlobalId(localDocumentId);
            var ownerNodeId = await ownershipManager.GetOrAcquireOwnerAsync(globalId, backplane.NodeId, ct);

            var payload = new ManagementRequestData(globalId, args);

            if (ownerNodeId == backplane.NodeId)
            {
                return await ExecuteLocallyAsync(command, payload, ct);
            }

            var response = await backplane.SendRequestAsync(
                ownerNodeId,
                backplaneCommand,
                JsonSerializer.SerializeToUtf8Bytes(payload, OpStreamJsonOptions.Default),
                ct);

            return response.Success
                ? OpResult.Ok()
                : OpResult.Fail(response.ErrorMessage ?? "Remote node execution failed");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error routing management command {Command} for {DocId}", command, localDocumentId);
            return OpResult.Fail(ex.Message);
        }
    }

    private async Task<OpResult> ExecuteLocallyAsync(DatabaseCommandType command, ManagementRequestData data, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var history = scope.ServiceProvider.GetRequiredService<IHistoryStore>();

        switch (command)
        {
            case DatabaseCommandType.DeleteDocument:
                await documentRouter.EvictSessionAsync(data.GlobalDocumentId, ct);
                await store.DeleteAsync(data.GlobalDocumentId, ct);
                try { await history.DeleteAsync(data.GlobalDocumentId, ct); }
                catch (NotSupportedException) { /* optional history wipe */ }

                // Notify the rest of the cluster so any cached state elsewhere is dropped.
                await backplane.PublishAsync(
                    OpStreamConstants.ManagementChannels.ClusterBroadcast,
                    new BackplaneMessage(
                        backplane.NodeId,
                        OpStreamConstants.BackplaneMessages.DocumentDeleted,
                        JsonSerializer.SerializeToUtf8Bytes(data.GlobalDocumentId, OpStreamJsonOptions.Default)),
                    ct);
                return OpResult.Ok();

            case DatabaseCommandType.CompactDocument:
                await compactWithAnchors.CompactAsync(data.GlobalDocumentId, ParseLong(data.Args, "upToRevision"), ct);
                return OpResult.Ok();

            case DatabaseCommandType.PurgeHistory:
                await history.PurgeUpToAsync(data.GlobalDocumentId, ParseLong(data.Args, "upToRevision"), ct);
                return OpResult.Ok();

            default:
                return OpResult.Fail($"Command {command} is not routable to an owner node.");
        }
    }

    // ─── IBackplaneRequestExtension ──────────────────────────────────────────

    public bool CanHandle(string type) => type switch
    {
        OpStreamConstants.BackplaneCommands.DeleteDocument => true,
        OpStreamConstants.BackplaneCommands.CompactDocument => true,
        OpStreamConstants.BackplaneCommands.PurgeHistory => true,
        _ => false
    };

    public async Task<BackplaneResponse> HandleAsync(BackplaneRequest request, CancellationToken ct = default)
    {
        try
        {
            var data = JsonSerializer.Deserialize<ManagementRequestData>(request.Payload.Span, OpStreamJsonOptions.Default)!;
            var command = request.Type switch
            {
                OpStreamConstants.BackplaneCommands.DeleteDocument => DatabaseCommandType.DeleteDocument,
                OpStreamConstants.BackplaneCommands.CompactDocument => DatabaseCommandType.CompactDocument,
                OpStreamConstants.BackplaneCommands.PurgeHistory => DatabaseCommandType.PurgeHistory,
                _ => throw new InvalidOperationException($"Unsupported command {request.Type}")
            };

            var result = await ExecuteLocallyAsync(command, data, ct);
            return result.Success
                ? new BackplaneResponse(request.RequestId, true, ReadOnlyMemory<byte>.Empty)
                : new BackplaneResponse(request.RequestId, false, ReadOnlyMemory<byte>.Empty, result.ErrorMessage);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling management backplane request {RequestId} of type {Type}", request.RequestId, request.Type);
            return new BackplaneResponse(request.RequestId, false, ReadOnlyMemory<byte>.Empty, ex.Message);
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async ValueTask<bool> AuthorizeAsync(DatabaseCommandContext ctx, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var authorizer = scope.ServiceProvider.GetRequiredService<IDatabaseCommandAuthorizer>();
        return await authorizer.AuthorizeAsync(ctx, ct);
    }

    private static OpResult<T> Forbidden<T>() => OpResult<T>.Fail("Forbidden: Insufficient permissions for this operation.");

    private static long ParseLong(IReadOnlyDictionary<string, string>? args, string key)
    {
        if (args is null || !args.TryGetValue(key, out var raw) || !long.TryParse(raw, out var value))
            throw new InvalidOperationException($"Missing or invalid argument '{key}'.");
        return value;
    }

    private record ManagementRequestData(string GlobalDocumentId, IReadOnlyDictionary<string, string>? Args);
}
