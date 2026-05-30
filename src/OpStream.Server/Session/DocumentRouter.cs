using Microsoft.Extensions.Logging;
using OpStream.Constants;
using OpStream.Server.Diagnostics;
using OpStream.Server.Multitenancy;
using OpStream.Server.Validation;
using OpStream.Shared.Abstractions;
using OpStream.Shared.Messages;
using System.Collections.Concurrent;

namespace OpStream.Server.Session;

// TODO: Move to a different location
public static class ProtocolVersions
{
    public const int Current = 1;
}

/// <summary>
/// Public entry point for the collaboration path. <see cref="DocumentRouter"/> is a thin
/// <b>facade</b>: it exposes the join / op / awareness / disconnect API the transports call and
/// orchestrates a set of single-responsibility collaborators —
/// <see cref="IDocumentExecutionPipeline"/> (auth + ownership + proxy),
/// <see cref="IDocumentSessionRegistry"/> (live sessions),
/// <see cref="IAwarenessSessionRegistry"/> (presence),
/// <see cref="IPeerRegistry"/> (peer→document membership),
/// <see cref="IDocumentDrainCoordinator"/> (last-peer-leaves handling),
/// <see cref="IDocumentDiagnosticsService"/>, and the
/// <see cref="IDocumentBackplaneGateway"/> (inbound owner-routed requests).
/// It still owns the small cross-cutting glue: per-document backplane subscriptions and the
/// idle-close timers.
/// </summary>
public class DocumentRouter(
    IDocumentExecutionPipeline pipeline,
    IDocumentSessionRegistry sessions,
    IAwarenessSessionRegistry awareness,
    IPeerRegistry peers,
    IDocumentDrainCoordinator drain,
    IDocumentDiagnosticsService diagnostics,
    IDocumentBackplaneGateway gateway,
    IDocumentLockRegistry locks,
    OpStreamStartupValidator startupValidator,
    IBackplane backplane,
    IDocumentOwnershipManager ownershipManager,
    IDocumentIdGlobalizer globalizer,
    ITimerFactory timerFactory,
    IInboundMessageValidationPipeline inboundValidation,
    SessionRegistryOptions options,
    ILogger<DocumentRouter> logger)
{
    private readonly ConcurrentDictionary<string, IAsyncDisposable> _backplaneSubscriptions = new();
    private readonly ConcurrentDictionary<string, ITimer> _idleTimers = new();

    /// <summary>
    /// Backplane this router uses. Exposed so management routers can publish fan-out messages on
    /// shared channels without duplicating the dependency.
    /// </summary>
    internal IBackplane Backplane => backplane;

    /// <summary>Raised when a per-document backplane message arrives (consumed by transport relays).</summary>
    public event Func<string, BackplaneMessage, Task>? OnBackplaneMessage;

    /// <summary>
    /// Logs the active infrastructure and wires up the inbound backplane request handler.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        startupValidator.Validate();
        await gateway.StartAsync(ct);
    }

    // ─── Public collaboration API ────────────────────────────────────────────

    /// <summary>Processes a peer joining a document (authorize → owner-route → open session).</summary>
    public async Task<OpResult<SessionJoinResult>> JoinDocumentAsync(
        string documentId,
        string documentType,
        string peerId,
        int protocolVersion,
        bool isProxied = false,
        CancellationToken ct = default)
    {
        if (protocolVersion != ProtocolVersions.Current)
            return OpResult<SessionJoinResult>.Fail($"UnsupportedProtocol: Required proto={ProtocolVersions.Current}");

        if (!isProxied)
        {
            var validation = await inboundValidation.ValidateAsync(
                new InboundMessage(InboundMessageKind.Join, peerId, documentId, documentType, protocolVersion), ct);
            if (!validation.IsValid)
                return OpResult<SessionJoinResult>.Fail($"InvalidMessage: {validation.Reason}");
        }

        var globalId = isProxied ? documentId : globalizer.ToGlobalId(documentId);

        peers.Track(peerId, globalId);
        CancelIdleClosure(globalId);

        var result = await pipeline.ExecuteAsync<SessionJoinResult, JoinRequestData>(
            globalId,
            isProxied,
            access => access.CanRead,
            OpStreamConstants.BackplaneCommands.JoinDocument,
            new JoinRequestData(globalId, documentType, peerId, protocolVersion),
            ct => sessions.GetOrOpenAsync(globalId, documentType, ct),
            async (session, innerCt) =>
            {
                var docResult = await session.JoinAsync(peerId, innerCt);
                var awarenessSession = await awareness.GetOrCreateAsync(globalId, innerCt);
                var currentAwareness = awarenessSession.GetStates().ToList();
                return new SessionJoinResult(docResult.Revision, docResult.Snapshot, docResult.PendingOps, currentAwareness);
            },
            ct);

        if (result.Success)
            await EnsureBackplaneSubscriptionAsync(globalId, ct);

        return result;
    }

    /// <summary>Applies an op, ensuring it runs on the authoritative owner node.</summary>
    public async Task<OpResult<OpApplyResult>> ApplyOpAsync(
        string peerId, string documentId, ReadOnlyMemory<byte> payload, long baseRevision,
        bool isProxied = false, CancellationToken ct = default)
    {
        if (!isProxied)
        {
            var validation = await inboundValidation.ValidateAsync(
                new InboundMessage(InboundMessageKind.Op, peerId, documentId, BaseRevision: baseRevision, Payload: payload), ct);
            if (!validation.IsValid)
                return OpResult<OpApplyResult>.Fail($"InvalidMessage: {validation.Reason}");
        }

        var globalId = isProxied ? documentId : globalizer.ToGlobalId(documentId);
        return await pipeline.ExecuteAsync<OpApplyResult, ApplyOpRequestData>(
            globalId,
            isProxied,
            access => access.CanWrite,
            OpStreamConstants.BackplaneCommands.ApplyOp,
            new ApplyOpRequestData(globalId, peerId, payload.ToArray(), baseRevision),
            _ => Task.FromResult(sessions.TryGet(globalId) ?? throw new Exception("Session not found on owner node.")),
            (session, innerCt) => session.ApplyOpAsync(peerId, payload, baseRevision, innerCt),
            ct);
    }

    /// <summary>Updates a peer's awareness (presence) state, distributed across the cluster.</summary>
    public async Task<OpResult<AwarenessState>> UpdateAwarenessAsync(
        string peerId, string documentId, System.Text.Json.JsonElement data,
        bool isProxied = false, CancellationToken ct = default)
    {
        if (!isProxied)
        {
            var validation = await inboundValidation.ValidateAsync(
                new InboundMessage(InboundMessageKind.Awareness, peerId, documentId, Data: data), ct);
            if (!validation.IsValid)
                return OpResult<AwarenessState>.Fail($"InvalidMessage: {validation.Reason}");
        }

        var globalId = isProxied ? documentId : globalizer.ToGlobalId(documentId);
        return await pipeline.ExecuteAsync<AwarenessState, UpdateAwarenessRequestData>(
            globalId,
            isProxied,
            access => access.CanRead,
            OpStreamConstants.BackplaneCommands.UpdateAwareness,
            new UpdateAwarenessRequestData(globalId, peerId, data),
            _ => Task.FromResult<IDocumentSession>(null!),
            async (_, innerCt) =>
            {
                var awarenessSession = await awareness.GetOrCreateAsync(globalId, innerCt);
                return await awarenessSession.UpdateAsync(peerId, data, innerCt);
            },
            ct);
    }

    /// <summary>
    /// Disconnects a peer from every document it was in. When a document loses its last peer it
    /// drains: host drain handlers are notified and may request deletion; otherwise an idle-close
    /// timer is armed.
    /// </summary>
    public async Task RemovePeerFromAllSessionsAsync(string peerId)
    {
        var globalIds = peers.Remove(peerId);
        if (globalIds.Count == 0) return;

        foreach (var globalId in globalIds)
        {
            var session = sessions.TryGet(globalId);
            if (session is not null)
            {
                await session.LeaveAsync(peerId);

                if (session.ActivePeersCount == 0)
                {
                    var decision = await drain.NotifyAsync(session);
                    if (decision == DocumentDrainDecision.Delete)
                    {
                        await CloseSessionAsync(globalId);
                        await drain.DeleteDataAsync(globalId);
                    }
                    else
                    {
                        ScheduleSessionClosure(globalId);
                    }
                }
            }

            var awarenessSession = awareness.TryGet(globalId);
            if (awarenessSession is not null)
                await awarenessSession.LeaveAsync(peerId);

            await backplane.PublishAsync(globalId, new BackplaneMessage(
                backplane.NodeId,
                OpStreamConstants.BackplaneMessages.PeerDisconnected,
                ReadOnlyMemory<byte>.Empty,
                peerId));
        }
    }

    // ─── Session lifecycle (facade-level orchestration) ──────────────────────

    /// <summary>Ids of every document with a live session on this node (global IDs).</summary>
    public IReadOnlyList<string> GetActiveDocumentIds() => sessions.ActiveDocumentIds;

    /// <summary>Returns the live session for a document, or null if not owned/open here.</summary>
    public IDocumentSession? TryGetActiveSession(string globalDocumentId) => sessions.TryGet(globalDocumentId);

    /// <summary>Documents a given peer is currently joined to (local IDs).</summary>
    public string[] GetDocumentsId(string peerId) => peers.DocumentsFor(peerId).Select(globalizer.ToLocalId).ToArray();

    /// <summary>Closes any local session/awareness/subscription/timer bound to the document and releases ownership.</summary>
    public async Task EvictSessionAsync(string globalDocumentId, CancellationToken ct = default)
    {
        await CloseSessionAsync(globalDocumentId);
        try
        {
            await ownershipManager.ReleaseOwnershipAsync(globalDocumentId, backplane.NodeId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to release ownership for document {DocId} during eviction", globalDocumentId);
        }
    }

    /// <summary>Fully closes a document on this node: session, awareness, subscription, idle timer, lock.</summary>
    public async Task CloseSessionAsync(string globalDocumentId)
    {
        await sessions.CloseAsync(globalDocumentId);
        await awareness.CloseAsync(globalDocumentId);

        if (_backplaneSubscriptions.TryRemove(globalDocumentId, out var sub))
            await sub.DisposeAsync();

        CancelIdleClosure(globalDocumentId);
        locks.Remove(globalDocumentId);
    }

    /// <summary>Builds a read-only diagnostic snapshot of a document.</summary>
    public Task<DocumentDiagnostics> GetDiagnosticsSnapshotAsync(
        string documentId, int recentOpCount = 50, CancellationToken ct = default)
    {
        var globalId = globalizer.ToGlobalId(documentId);
        return diagnostics.GetSnapshotAsync(globalId, recentOpCount, ct);
    }

    // ─── Cross-cutting glue ──────────────────────────────────────────────────

    private async Task EnsureBackplaneSubscriptionAsync(string documentId, CancellationToken ct)
    {
        if (_backplaneSubscriptions.ContainsKey(documentId)) return;

        var sub = await backplane.SubscribeAsync(documentId, async msg =>
        {
            if (OnBackplaneMessage != null)
                await OnBackplaneMessage(documentId, msg);
        }, ct);

        if (!_backplaneSubscriptions.TryAdd(documentId, sub))
            await sub.DisposeAsync(); // someone beat us to it
    }

    private void ScheduleSessionClosure(string documentId)
    {
        var timer = timerFactory.CreateTimer(
            async _ => await CloseSessionAsync(documentId),
            null,
            options.IdleTimeout,
            Timeout.InfiniteTimeSpan);

        _idleTimers.AddOrUpdate(documentId, timer, (_, old) => { old.Dispose(); return timer; });
    }

    private void CancelIdleClosure(string documentId)
    {
        if (_idleTimers.TryRemove(documentId, out var timer))
            timer.Dispose();
    }
}
