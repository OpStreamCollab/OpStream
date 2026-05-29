using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpStream.Constants;

using OpStream.Server.Storage;
using OpStream.Shared.Abstractions;
using OpStream.Shared.Messages;
using System.Collections.Concurrent;
using System.Text.Json;


namespace OpStream.Server.Session;


// TODO: Move to a different location
public static class ProtocolVersions
{
    public const int Current = 1;
}

/// <summary>
/// Orchestrates the lifecycle and distribution of document sessions across the cluster.
/// Handles authorization, routing to the owner node, and management of document and awareness state.
/// </summary>
public class DocumentRouter(
    IServiceProvider serviceProvider,
    IServiceScopeFactory scopeFactory,
    IEnumerable<IDocumentSessionFactory> factories,
    IBackplane backplane,
    IDocumentOwnershipManager ownershipManager,
    ILogger<DocumentRouter> logger)
{

    Dictionary<string, IDocumentSessionFactory> _factories = factories.ToDictionary(f => f.DocumentType, f => f, StringComparer.OrdinalIgnoreCase);

    // Resolved lazily to break the circular dependency:
    // DocumentRouter → IBackplaneRequestExtension → DatabaseCommandRouter/CommentRouter → DocumentRouter
    private IReadOnlyList<IBackplaneRequestExtension>? _requestExtensions;
    private IReadOnlyList<IBackplaneRequestExtension> RequestExtensions =>
        _requestExtensions ??= serviceProvider.GetServices<IBackplaneRequestExtension>().ToArray();

    /// <summary>
    /// Backplane that this router uses. Exposed so management routers can publish fan-out
    /// messages on shared channels without duplicating the dependency.
    /// </summary>
    internal IBackplane Backplane => backplane;

    private readonly ConcurrentDictionary<string, IDocumentSession> _activeSessions = new();

    private readonly ConcurrentDictionary<string, IAwarenessSession> _activeAwareness = new();

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _peerDocuments = new();

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new();

    private readonly ConcurrentDictionary<string, IAsyncDisposable> _backplaneSubscriptions = new();

    private readonly ConcurrentDictionary<string, ITimer> _idleTimers = new();
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Event triggered when a backplane message is received.
    /// </summary>
    public event Func<string, BackplaneMessage, Task>? OnBackplaneMessage;

    /// <summary>
    /// Initializes the router by registering the backplane request handler.
    /// Logs warnings when default (non-production) infrastructure is detected.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // ── Storage check ────────────────────────────────────────────────────
        var store = serviceProvider.GetRequiredService<IDocumentStore>();
        if (store is MemoryDocumentStore)
            logger.LogWarning(
                "OpStream is using MemoryDocumentStore. All document data will be lost when " +
                "the process restarts. Call UseRedisStorage(), UseEfCoreStorage(), or another " +
                "persistent store before going to production.");
        else
            logger.LogInformation("OpStream storage: {StoreType}", store.GetType().Name);

        // ── Backplane check ──────────────────────────────────────────────────
        if (backplane is LocalBackplane)
            logger.LogInformation(
                "OpStream is running in single-node mode (LocalBackplane). " +
                "Call UseRedisBackplane() or UseNatsBackplane() for multi-node deployments.");
        else
            logger.LogInformation("OpStream backplane: {BackplaneType}", backplane.GetType().Name);

        // ── Registered engines ───────────────────────────────────────────────
        foreach (var (docType, _) in _factories)
            logger.LogInformation("OpStream engine registered for document type: \"{DocType}\"", docType);

        await backplane.RegisterRequestHandlerAsync(HandleIncomingRequestAsync, ct);
    }

    /// <summary>
    /// Dispatches incoming backplane requests to the appropriate local methods.
    /// </summary>
    /// <param name="request">The incoming backplane request.</param>
    /// <returns>A backplane response containing the result of the request.</returns>
    private async Task<BackplaneResponse> HandleIncomingRequestAsync(BackplaneRequest request)
    {
        try
        {
            if (request.Type == OpStreamConstants.BackplaneCommands.JoinDocument)
            {
                var joinData = JsonSerializer.Deserialize<JoinRequestData>(request.Payload.Span, OpStreamJsonOptions.Default)!;
                var joinResult = await JoinDocumentAsync(joinData.DocumentId, joinData.DocumentType, joinData.PeerId, joinData.ProtocolVersion, isProxied: true);

                if (!joinResult.Success) return new BackplaneResponse(request.RequestId, false, ReadOnlyMemory<byte>.Empty, joinResult.ErrorMessage);

                return new BackplaneResponse(request.RequestId, true, JsonSerializer.SerializeToUtf8Bytes(joinResult.Value, OpStreamJsonOptions.Default));
            }
            else if (request.Type == OpStreamConstants.BackplaneCommands.ApplyOp)
            {
                var opData = JsonSerializer.Deserialize<ApplyOpRequestData>(request.Payload.Span, OpStreamJsonOptions.Default)!;
                var opResult = await ApplyOpAsync(opData.PeerId, opData.DocumentId, opData.Payload, opData.BaseRevision, isProxied: true);

                if (!opResult.Success) return new BackplaneResponse(request.RequestId, false, ReadOnlyMemory<byte>.Empty, opResult.ErrorMessage);

                return new BackplaneResponse(request.RequestId, true, JsonSerializer.SerializeToUtf8Bytes(opResult.Value, OpStreamJsonOptions.Default));
            }
            else if (request.Type == OpStreamConstants.BackplaneCommands.UpdateAwareness)
            {
                var awarenessData = JsonSerializer.Deserialize<UpdateAwarenessRequestData>(request.Payload.Span, OpStreamJsonOptions.Default)!;
                var awarenessResult = await UpdateAwarenessAsync(awarenessData.PeerId, awarenessData.DocumentId, awarenessData.Data, isProxied: true);

                if (!awarenessResult.Success) return new BackplaneResponse(request.RequestId, false, ReadOnlyMemory<byte>.Empty, awarenessResult.ErrorMessage);

                return new BackplaneResponse(request.RequestId, true, JsonSerializer.SerializeToUtf8Bytes(awarenessResult.Value, OpStreamJsonOptions.Default));
            }
            else
            {
                foreach (var extension in RequestExtensions)
                {
                    if (extension.CanHandle(request.Type))
                        return await extension.HandleAsync(request);
                }
                return new BackplaneResponse(request.RequestId, false, ReadOnlyMemory<byte>.Empty, $"Unknown request type: {request.Type}");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling incoming backplane request {RequestId} of type {Type}", request.RequestId, request.Type);
            return new BackplaneResponse(request.RequestId, false, ReadOnlyMemory<byte>.Empty, ex.Message);
        }
    }

    /// <summary>
    /// Closes any local session/awareness/timer/subscription bound to the document and
    /// releases ownership. Safe to call on a node that doesn't currently own the document.
    /// </summary>
    public async Task EvictSessionAsync(string documentId, CancellationToken ct = default)
    {
        await CloseSessionAsync(documentId);
        try
        {
            await ownershipManager.ReleaseOwnershipAsync(documentId, backplane.NodeId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to release ownership for document {DocId} during eviction", documentId);
        }
    }

    /// <summary>
    /// Returns the ids of every document currently owned by this node (i.e. has an active
    /// in-memory session). Used by tenant-wide eviction fan-outs.
    /// </summary>
    public IReadOnlyList<string> GetActiveDocumentIds() => _activeSessions.Keys.ToArray();

    /// <summary>
    /// Returns the active in-memory session for <paramref name="documentId"/>, or <c>null</c>
    /// if this node does not currently own a session for that document. Does NOT open or load a
    /// session — strictly a lookup against existing state.
    /// </summary>
    public IDocumentSession? TryGetActiveSession(string documentId)
    {
        _activeSessions.TryGetValue(documentId, out var session);
        return session;
    }

    /// <summary>
    /// Processes a request for a peer to join a document session.
    /// Orchestrates authorization, protocol validation, and distributed session resolution.
    /// </summary>
    /// <param name="documentId">The ID of the document to join.</param>
    /// <param name="documentType">The type of the document.</param>
    /// <param name="peerId">The ID of the peer joining.</param>
    /// <param name="protocolVersion">The protocol version used by the client.</param>
    /// <param name="isProxied">Whether the request is being proxied from another node.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>An <see cref="OpResult{T}"/> containing the join result.</returns>
    public async Task<OpResult<SessionJoinResult>> JoinDocumentAsync(
        string documentId,
        string documentType,
        string peerId,
        int protocolVersion,
        bool isProxied = false,
        CancellationToken ct = default)
    {
        if (protocolVersion != ProtocolVersions.Current)
        {
            return OpResult<SessionJoinResult>.Fail($"UnsupportedProtocol: Required proto={ProtocolVersions.Current}");
        }

        var docs = _peerDocuments.GetOrAdd(peerId, _ => new ConcurrentDictionary<string, byte>());
        docs.TryAdd(documentId, 0);

        if (_idleTimers.TryRemove(documentId, out var timer))
        {
            await timer.DisposeAsync();
        }

        var result = await ExecuteRoutedAsync<SessionJoinResult, JoinRequestData>(
            documentId,
            isProxied,
            access => access.CanRead,
            OpStreamConstants.BackplaneCommands.JoinDocument,
            new JoinRequestData(documentId, documentType, peerId, protocolVersion),
            async (ct) => {
                var session = await GetSessionAsync(documentId, ct);
                return session ?? await OpenSessionAsync(documentId, documentType, ct);
            },
            async (session, ct) => {
                var docResult = await session.JoinAsync(peerId, ct);

                var awarenessSession = await GetAwarenessSessionAsync(documentId, ct);
                var currentAwareness = awarenessSession.GetStates().ToList();

                return new SessionJoinResult(docResult.Revision, docResult.Snapshot, docResult.PendingOps, currentAwareness);
            },
            ct);

        if (result.Success)
        {
            await EnsureBackplaneSubscriptionAsync(documentId, ct);
        }

        return result;
    }

    /// <summary>
    /// Applies an operational transform operation to a document session.
    /// Ensures the operation is processed by the authoritative owner node.
    /// </summary>
    /// <param name="peerId">The ID of the peer applying the operation.</param>
    /// <param name="documentId">The ID of the document.</param>
    /// <param name="payload">The operation payload.</param>
    /// <param name="baseRevision">The base revision for the operation.</param>
    /// <param name="isProxied">Whether the request is being proxied from another node.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>An <see cref="OpResult{T}"/> containing the operation result.</returns>
    public async Task<OpResult<OpApplyResult>> ApplyOpAsync(string peerId, string documentId, ReadOnlyMemory<byte> payload, long baseRevision, bool isProxied = false, CancellationToken ct = default)
    {
        return await ExecuteRoutedAsync<OpApplyResult, ApplyOpRequestData>(
            documentId,
            isProxied,
            access => access.CanWrite,
            OpStreamConstants.BackplaneCommands.ApplyOp,
            new ApplyOpRequestData(documentId, peerId, payload.ToArray(), baseRevision),
            async (ct) => await GetSessionAsync(documentId, ct) ?? throw new Exception("Session not found on owner node."),
            (session, ct) => session.ApplyOpAsync(peerId, payload, baseRevision, ct),
            ct);
    }

    /// <summary>
    /// Updates the awareness (presence) data for a peer.
    /// Awareness data is distributed across the cluster but not persisted.
    /// </summary>
    /// <param name="peerId">The ID of the peer updating their awareness.</param>
    /// <param name="documentId">The ID of the document.</param>
    /// <param name="data">The awareness data.</param>
    /// <param name="isProxied">Whether the request is being proxied from another node.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>An <see cref="OpResult{T}"/> containing the updated awareness state.</returns>
    public async Task<OpResult<AwarenessState>> UpdateAwarenessAsync(string peerId, string documentId, JsonElement data, bool isProxied = false, CancellationToken ct = default)
    {
        return await ExecuteRoutedAsync<AwarenessState, UpdateAwarenessRequestData>(
            documentId,
            isProxied,
            access => access.CanRead, 
            OpStreamConstants.BackplaneCommands.UpdateAwareness,
            new UpdateAwarenessRequestData(documentId, peerId, data),
            async (ct) => null!, 
            async (_, ct) => {
                var awarenessSession = await GetAwarenessSessionAsync(documentId, ct);
                return await awarenessSession.UpdateAsync(peerId, data, ct);
            },
            ct);
    }

    /// <summary>
    /// Internal generic orchestrator that enforces authorization and routes operations to the authoritative node.
    /// </summary>
    private async Task<OpResult<TResult>> ExecuteRoutedAsync<TResult, TRequestData>(
        string documentId,
        bool isProxied,
        Func<DocumentAccess, bool> permissionCheck,
        string backplaneCommand,
        TRequestData proxyData,
        Func<CancellationToken, Task<IDocumentSession>> sessionProvider,
        Func<IDocumentSession, CancellationToken, Task<TResult>> localAction,
        CancellationToken ct)
    {
        try
        {
            if (!isProxied)
            {
                using var scope = scopeFactory.CreateScope();
                var authorizer = scope.ServiceProvider.GetRequiredService<IDocumentAuthorizer>();
                var access = await authorizer.AuthorizeAsync(documentId, ct);
                if (!permissionCheck(access))
                {
                    return OpResult<TResult>.Fail("Forbidden: Insufficient permissions for this operation.");
                }
            }

            var ownerNodeId = await ownershipManager.GetOrAcquireOwnerAsync(documentId, backplane.NodeId, ct);

            if (ownerNodeId == backplane.NodeId)
            {
                var session = await sessionProvider(ct);
                var val = await localAction(session, ct);
                return OpResult<TResult>.Ok(val);
            }
            else
            {
                var response = await backplane.SendRequestAsync(ownerNodeId, backplaneCommand, JsonSerializer.SerializeToUtf8Bytes(proxyData, OpStreamJsonOptions.Default), ct);
                if (!response.Success)
                {
                    return OpResult<TResult>.Fail(response.ErrorMessage ?? "Remote node execution failed");
                }
                var val = JsonSerializer.Deserialize<TResult>(response.Payload.Span, OpStreamJsonOptions.Default)!;
                return OpResult<TResult>.Ok(val);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in ExecuteRoutedAsync for document {DocId} command {Command}", documentId, backplaneCommand);
            return OpResult<TResult>.Fail(ex.Message);
        }
    }

    private record JoinRequestData(string DocumentId, string DocumentType, string PeerId, int ProtocolVersion);
    private record ApplyOpRequestData(string DocumentId, string PeerId, byte[] Payload, long BaseRevision);
    private record UpdateAwarenessRequestData(string DocumentId, string PeerId, JsonElement Data);

    /// <summary>
    /// Ensures that the local node is subscribed to backplane events for the specified document.
    /// </summary>
    /// <param name="documentId">The ID of the document to subscribe to.</param>
    /// <param name="ct">The cancellation token.</param>
    private async Task EnsureBackplaneSubscriptionAsync(string documentId, CancellationToken ct)
    {
        if (!_backplaneSubscriptions.ContainsKey(documentId))
        {
            var sub = await backplane.SubscribeAsync(documentId, async msg =>
            {
                if (OnBackplaneMessage != null)
                {
                    await OnBackplaneMessage(documentId, msg);
                }
            }, ct);
            _backplaneSubscriptions.TryAdd(documentId, sub);
        }
    }

    /// <summary>
    /// Retrieves or initializes the awareness session for a document.
    /// </summary>
    /// <param name="documentId">The ID of the document.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The awareness session.</returns>
    private async Task<IAwarenessSession> GetAwarenessSessionAsync(string documentId, CancellationToken ct = default)
    {
        if (_activeAwareness.TryGetValue(documentId, out var existing))
        {
            return existing;
        }

        var sessionLock = _sessionLocks.GetOrAdd(documentId, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(ct);
        try
        {
            if (_activeAwareness.TryGetValue(documentId, out var awareness))
            {
                return awareness;
            }

            var newSession = AwarenessSession.CreateDefault(documentId, backplane);
            _activeAwareness.TryAdd(documentId, newSession);
            return newSession;
        }
        finally
        {
            sessionLock.Release();
        }
    }

    /// <summary>
    /// Retrieves an active document session from local memory if it exists.
    /// </summary>
    /// <param name="documentId">The ID of the document.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The document session, or null if not found.</returns>
    private async Task<IDocumentSession> GetSessionAsync(string documentId, CancellationToken ct = default)
    {

        if (_activeSessions.TryGetValue(documentId, out var existingSession))
        {
            return existingSession;
        }
        var sessionLock = _sessionLocks.GetOrAdd(documentId, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(ct);

        try
        {
            if (_activeSessions.TryGetValue(documentId, out var session))
            {
                return session;
            }
            return default!;
        }
        finally
        {
            sessionLock.Release();
        }
    }


    /// <summary>
    /// Opens a new document session by loading state from storage and hydrating pending operations.
    /// </summary>
    /// <param name="documentId">The ID of the document.</param>
    /// <param name="documentType">The type of the document.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The opened document session.</returns>
    private async Task<IDocumentSession> OpenSessionAsync(string documentId, string documentType, CancellationToken ct = default)
    {
        if (_activeSessions.TryGetValue(documentId, out var existingSession))
        {
            return existingSession;
        }

        var sessionLock = _sessionLocks.GetOrAdd(documentId, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(ct);

        try
        {
            if (_activeSessions.TryGetValue(documentId, out var session))
            {
                return session;
            }

            if (!_factories.TryGetValue(documentType, out var factory))
            {
                throw new NotSupportedException($"No session engine registered for document type: {documentType}");
            }

            using var scope = serviceProvider.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

            var snapshot = await store.LoadSnapshotAsync(documentId, ct);
            long currentRevision = snapshot?.Revision ?? 0;

            var newSession = await factory.CreateSessionAsync(documentId, currentRevision, snapshot?.State, ct);

            var opsStream = store.StreamOpsAsync(documentId, currentRevision, ct);
            await foreach (var storedOp in opsStream)
            {

                await newSession.RehydrateOpAsync(storedOp);
            }

            _activeSessions.TryAdd(documentId, newSession);
            return newSession;

        }
        finally
        {
            sessionLock.Release();
        }
    }



    /// <summary>
    /// Gets the list of document IDs that a specific peer is currently connected to.
    /// </summary>
    /// <param name="peerId">The ID of the peer.</param>
    /// <returns>An array of document IDs.</returns>
    public string[] GetDocumentsId(string peerId)
    {
        if (_peerDocuments.TryGetValue(peerId, out var documentIds))
        {
            return [.. documentIds.Keys];
        }
        return [];
    }

    /// <summary>
    /// Disconnects a peer from all document sessions they are active in.
    /// Schedules automatic session closure if the document becomes idle.
    /// </summary>
    /// <param name="peerId">The ID of the peer to remove.</param>
    public async Task RemovePeerFromAllSessionsAsync(string peerId)
    {
        if (!_peerDocuments.TryRemove(peerId, out var documentIds))
        {
            return;
        }

        foreach (var documentId in documentIds.Keys)
        {
            if (_activeSessions.TryGetValue(documentId, out var session))
            {
                await session.LeaveAsync(peerId);

                if (session.ActivePeersCount == 0)
                {
                    ScheduleSessionClosure(documentId);
                }
            }

            if (_activeAwareness.TryGetValue(documentId, out var awarenessSession))
            {
                await awarenessSession.LeaveAsync(peerId);
            }

            // Notify the backplane so other nodes know this peer has left
            var backplaneMsg = new BackplaneMessage(
                backplane.NodeId,
                OpStreamConstants.BackplaneMessages.PeerDisconnected,
                ReadOnlyMemory<byte>.Empty,
                peerId);

            await backplane.PublishAsync(documentId, backplaneMsg);
        }
    }

    /// <summary>
    /// Schedules a timer to close a document session if no new peers join within the timeout period.
    /// </summary>
    /// <param name="documentId">The ID of the document.</param>
    private void ScheduleSessionClosure(string documentId)
    {
        var timer = serviceProvider.GetRequiredService<ITimerFactory>().CreateTimer(
            async _ => await CloseSessionAsync(documentId),
            null,
            IdleTimeout,
            Timeout.InfiniteTimeSpan);

        _idleTimers.AddOrUpdate(documentId, timer, (_, old) => { old.Dispose(); return timer; });
    }

    /// <summary>
    /// Returns a diagnostic snapshot of a document — used by the
    /// <c>GET /opstream/diag/{docId}</c> endpoint.
    /// <para>
    /// If the document is owned by this node, peer list and revision come from
    /// the in-memory session. Otherwise only the on-disk view is reported.
    /// </para>
    /// </summary>
    /// <param name="documentId">The document to inspect.</param>
    /// <param name="recentOpCount">How many most-recent ops to include (default 50).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A diagnostic snapshot of the document.</returns>
    public async Task<OpStream.Server.Diagnostics.DocumentDiagnostics> GetDiagnosticsSnapshotAsync(
        string documentId,
        int recentOpCount = 50,
        CancellationToken ct = default)
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
        bool activeHere = _activeSessions.TryGetValue(documentId, out var session);
        if (activeHere && session is not null)
        {
            revision = session.CurrentRevision;
            peers = session.Peers.ToArray();
        }

        // Pull the tail of the op log from storage.
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<OpStream.Server.Storage.IDocumentStore>();

        long from = Math.Max(0, revision - recentOpCount);
        var recentOps = new List<OpStream.Server.Diagnostics.DiagnosticsOpEntry>(recentOpCount);
        await foreach (var op in store.StreamOpsAsync(documentId, from, ct))
        {
            recentOps.Add(new OpStream.Server.Diagnostics.DiagnosticsOpEntry(
                op.Revision, op.AuthorId, op.Timestamp, op.Payload.Length, op.EngineType));
            if (recentOps.Count >= recentOpCount) break;
        }
        // Fallback: if we didn't have a revision from the session, infer it from the tail.
        if (revision == 0 && recentOps.Count > 0)
            revision = recentOps[^1].Revision;

        return new OpStream.Server.Diagnostics.DocumentDiagnostics(
            DocumentId: documentId,
            ActiveOnThisNode: activeHere,
            OwnerNodeId: ownerNodeId,
            Revision: revision,
            PeerCount: peers.Count,
            Peers: peers,
            RecentOps: recentOps);
    }

    /// <summary>
    /// Terminates a document session and its awareness manager, releasing all associated resources.
    /// </summary>
    /// <param name="documentId">The ID of the document to close.</param>
    public async Task CloseSessionAsync(string documentId)
    {
        logger.LogInformation("Closing session for document {DocId} due to inactivity or manual request", documentId);

        if (_activeSessions.TryRemove(documentId, out var session))
        {
            await session.DisposeAsync();
        }

        if (_activeAwareness.TryRemove(documentId, out var awarenessSession))
        {
            await awarenessSession.DisposeAsync();
        }

        if (_backplaneSubscriptions.TryRemove(documentId, out var sub))
        {
            await sub.DisposeAsync();
        }

        if (_idleTimers.TryRemove(documentId, out var timer))
        {
            await timer.DisposeAsync();
        }

        if (_sessionLocks.TryRemove(documentId, out var sem))
        {
            sem.Dispose();
        }
    }
}
