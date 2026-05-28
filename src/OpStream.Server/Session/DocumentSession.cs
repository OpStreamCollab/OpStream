using OpStream.Constants;
using OpStream.Server.Comments;
using OpStream.Server.Diagnostics;
using OpStream.Server.Engine;
using OpStream.Server.Models;
using OpStream.Server.Storage;
using OpStream.Shared.Abstractions;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using OpStream.Server.Snapshots;

namespace OpStream.Server.Session;

/// <summary>
/// Manages a collaborative session for a single document.
/// </summary>
/// <typeparam name="TDoc">The type of the document.</typeparam>
/// <typeparam name="TOp">The type of the operations.</typeparam>
public class DocumentSession<TDoc, TOp> : IDocumentSession
{
    private readonly IOpEngine<TDoc, TOp> _engine;
    private readonly IDocumentStore _store;
    private readonly IBackplane _backplane;
    private readonly ILogger<DocumentSession<TDoc, TOp>> _logger;

    private readonly SemaphoreSlim _lock = new(1, 1); // Serializes operation processing
    private TDoc _currentState;

    private readonly ConcurrentDictionary<string, int> _activePeers = new();
    private readonly IEnumerable<IOpValidator<TOp>> _validators;
    private readonly IReadOnlyList<IPostApplyHook<TOp>> _postApplyHooks;

    /// <summary>Unique identifier of the document this session represents.</summary>
    public string DocumentId { get; }

    /// <summary>The latest accepted revision number.</summary>
    public long CurrentRevision { get; private set; }

    /// <summary>Number of peers currently connected to this session.</summary>
    public int ActivePeersCount => _activePeers.Count;

    /// <inheritdoc/>
    public IReadOnlyCollection<string> Peers => _activePeers.Keys.ToArray();

    private readonly IOpSnapshotter _opSnapshotter;
    private readonly IOpHistorySnapshotter _historySnapshotter;

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentSession{TDoc, TOp}"/> class.
    /// </summary>
    /// <param name="documentId">The ID of the document.</param>
    /// <param name="initialState">The initial state of the document.</param>
    /// <param name="engine">The operation engine.</param>
    /// <param name="initialRevision">The initial revision number.</param>
    /// <param name="store">The document store.</param>
    /// <param name="backplane">The backplane for cluster communication.</param>
    /// <param name="opSnapshotter">The snapshotter for document states.</param>
    /// <param name="historySnapshotter">The snapshotter for operation history.</param>
    /// <param name="validators">The validators for incoming operations.</param>
    /// <param name="logger">The logger instance.</param>
    public DocumentSession(
        string documentId,
        TDoc initialState,
        IOpEngine<TDoc, TOp> engine,
        long initialRevision,
        IDocumentStore store,
        IBackplane backplane,
        IOpSnapshotter opSnapshotter,
        IOpHistorySnapshotter historySnapshotter,
        IEnumerable<IOpValidator<TOp>> validators,
        ILogger<DocumentSession<TDoc, TOp>> logger,
        IEnumerable<IPostApplyHook<TOp>>? postApplyHooks = null)
    {
        DocumentId = documentId;
        _currentState = initialState;
        CurrentRevision = initialRevision;
        _engine = engine;
        _store = store;
        _backplane = backplane;
        _opSnapshotter = opSnapshotter;
        _historySnapshotter = historySnapshotter;
        _validators = validators;
        _logger = logger;
        _postApplyHooks = (postApplyHooks ?? Array.Empty<IPostApplyHook<TOp>>()).ToArray();

        OpStreamTelemetry.ActiveDocuments.Add(1);
        OpStreamEventSource.Log.AdjustActiveDocuments(1);
    }

    /// <summary>
    /// Applies an operation to the document state.
    /// </summary>
    /// <param name="peerId">The ID of the peer applying the operation.</param>
    /// <param name="payload">The operation payload.</param>
    /// <param name="baseRevision">The base revision the operation is based on.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>An <see cref="OpApplyResult"/> containing the result of the operation.</returns>
    public async Task<OpApplyResult> ApplyOpAsync(string peerId, ReadOnlyMemory<byte> payload, long baseRevision, CancellationToken ct = default)
    {
        long startTimestamp = Stopwatch.GetTimestamp();

        // Parent activity — child of the inbound transport request span (HTTP/WS/gRPC).
        using var activity = OpStreamTelemetry.ActivitySource.StartActivity("opstream.session.apply_op");
        activity?.SetTag("doc.id", DocumentId);
        activity?.SetTag("peer.id", peerId);
        activity?.SetTag("op.base_revision", baseRevision);
        activity?.SetTag("op.size", payload.Length);
        activity?.SetTag("engine.name", typeof(TOp).Name);

        // Structured-logging scope. Every log line emitted from this method gets
        // doc.id / peer.id automatically without re-stating them.
        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["doc.id"] = DocumentId,
            ["peer.id"] = peerId
        });

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Op received (size={Size}B, baseRevision={BaseRevision})",
                payload.Length, baseRevision);
        }

        int transformCount = 0;
        bool lockAcquired = false;
        try
        {
            // 1. Deserialize raw JSON payload to our strong TOp type
            var incomingOp = JsonSerializer.Deserialize<TOp>(payload.Span, OpStreamJsonOptions.Default);
            if (incomingOp == null)
                throw new InvalidOperationException("Deserialized op payload is null.");

            // 2. Run validators
            var validationCtx = new OpValidationContext<TOp>(DocumentId, incomingOp);
            foreach (var validator in _validators)
            {
                if (!await validator.ValidateAsync(validationCtx, ct))
                {
                    var reason = $"Rejected by validator: {validator.GetType().Name}";
                    _logger.LogWarning("Op rejected: {Reason}", reason);
                    activity?.SetStatus(ActivityStatusCode.Error, reason);
                    OpStreamTelemetry.OperationsRejected.Add(1);
                    OpStreamEventSource.Log.OpRejected();
                    return new OpApplyResult(false, CurrentRevision, reason);
                }
            }

            // 3. Lock the session to ensure strict monotonic ordering
            await _lock.WaitAsync(ct);
            lockAcquired = true;

            // 4. Rebase against any concurrent ops the client did not see
            if (baseRevision < CurrentRevision)
            {
                using var transformActivity = OpStreamTelemetry.ActivitySource.StartActivity("opstream.engine.transform");
                transformActivity?.SetTag("doc.id", DocumentId);

                long readStart = Stopwatch.GetTimestamp();
                var pastOps = _store.StreamOpsAsync(DocumentId, baseRevision, ct);
                long expectedRevision = baseRevision + 1;

                await foreach (var storedOp in pastOps)
                {
                    if (storedOp.Revision != expectedRevision)
                    {
                        var reason = "Cannot reconstruct transformation path: compacted log past baseRevision.";
                        _logger.LogWarning("Op rejected: {Reason} (expected={Expected}, found={Found})",
                            reason, expectedRevision, storedOp.Revision);
                        activity?.SetStatus(ActivityStatusCode.Error, reason);
                        OpStreamTelemetry.OperationsRejected.Add(1);
                        OpStreamEventSource.Log.OpRejected();
                        return new OpApplyResult(false, CurrentRevision, reason);
                    }
                    expectedRevision++;

                    var existingOp = JsonSerializer.Deserialize<TOp>(storedOp.Payload.Span, OpStreamJsonOptions.Default)!;
                    incomingOp = _engine.Transform(incomingOp, existingOp, TransformPriority.ExistingWins);
                    transformCount++;

                    if (incomingOp == null || _engine.IsNoOp(incomingOp))
                    {
                        _logger.LogDebug("Op fully absorbed by concurrent history after {Count} transform(s)", transformCount);
                        transformActivity?.SetTag("transforms", transformCount);
                        OpStreamTelemetry.TransformCountPerOp.Record(transformCount);
                        return new OpApplyResult(true, CurrentRevision);
                    }
                }

                transformActivity?.SetTag("transforms", transformCount);
                OpStreamTelemetry.StoreReadLatency.RecordElapsedMs(readStart);

                if (expectedRevision <= CurrentRevision)
                {
                    var reason = "Cannot reconstruct transformation path: op log gap.";
                    _logger.LogWarning("Op rejected: {Reason}", reason);
                    activity?.SetStatus(ActivityStatusCode.Error, reason);
                    OpStreamTelemetry.OperationsRejected.Add(1);
                    OpStreamEventSource.Log.OpRejected();
                    return new OpApplyResult(false, CurrentRevision, reason);
                }
            }

            // 5. Apply to in-memory state
            using (var applyActivity = OpStreamTelemetry.ActivitySource.StartActivity("opstream.engine.apply"))
            {
                applyActivity?.SetTag("doc.id", DocumentId);
                _currentState = _engine.Apply(_currentState, incomingOp);
                CurrentRevision++;
            }

            // 6. Persist (we always store the TRANSFORMED op)
            var transformedPayload = JsonSerializer.SerializeToUtf8Bytes(incomingOp, OpStreamJsonOptions.Default);
            var newStoredOp = new StoredOp(
                Revision: CurrentRevision,
                AuthorId: peerId,
                Timestamp: DateTimeOffset.UtcNow,
                Payload: transformedPayload,
                EngineType: typeof(TOp).Name);

            using (var storeActivity = OpStreamTelemetry.ActivitySource.StartActivity("opstream.store.append"))
            {
                storeActivity?.SetTag("doc.id", DocumentId);
                storeActivity?.SetTag("revision", CurrentRevision);

                long appendStart = Stopwatch.GetTimestamp();
                await _store.AppendOpAsync(DocumentId, newStoredOp, ct);
                OpStreamTelemetry.StoreAppendLatency.RecordElapsedMs(appendStart);
            }

            await _historySnapshotter.AppendOpAsync(DocumentId, newStoredOp, ct);
            await _opSnapshotter.OpAddedAsync(_currentState, DocumentId, CurrentRevision, OpStreamJsonOptions.Default, ct);

            // 6b. Post-apply hooks (anchor rebases, audit sinks, …). Still under the session
            // lock so any side-effects stay consistent with the op log.
            var anchorUpdates = await RunPostApplyHooksAsync(incomingOp, peerId, isRehydration: false, ct);

            // 7. Broadcast
            using (var bpActivity = OpStreamTelemetry.ActivitySource.StartActivity("opstream.backplane.publish"))
            {
                bpActivity?.SetTag("doc.id", DocumentId);
                bpActivity?.SetTag("revision", CurrentRevision);

                var broadcastPayload = new OpAppliedBackplanePayload(transformedPayload, CurrentRevision, anchorUpdates);
                var backplaneMsg = new BackplaneMessage(
                    _backplane.NodeId,
                    OpStreamConstants.BackplaneMessages.OpApplied,
                    JsonSerializer.SerializeToUtf8Bytes(broadcastPayload, OpStreamJsonOptions.Default),
                    peerId);

                long publishStart = Stopwatch.GetTimestamp();
                await _backplane.PublishAsync(DocumentId, backplaneMsg, ct);
                OpStreamTelemetry.BackplanePublishLatency.RecordElapsedMs(publishStart);
                // Fanout is approximated by the local peer count (peers on other nodes
                // are counted in their own broadcast; with a real cluster-wide view
                // this should be replaced by the global peer set).
                OpStreamTelemetry.BroadcastFanout.Record(Math.Max(0, _activePeers.Count - 1));
            }

            OpStreamTelemetry.TransformCountPerOp.Record(transformCount);
            OpStreamTelemetry.PeersPerDocument.Record(_activePeers.Count);
            OpStreamTelemetry.OperationsProcessed.Add(1);
            OpStreamEventSource.Log.OpApplied();

            activity?.SetTag("op.new_revision", CurrentRevision);
            activity?.SetTag("op.transforms", transformCount);

            return new OpApplyResult(true, CurrentRevision);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected on disconnect; don't log as error.
            activity?.SetStatus(ActivityStatusCode.Error, "cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying op from peer {PeerId}", peerId);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            OpStreamTelemetry.OperationsRejected.Add(1);
            OpStreamEventSource.Log.OpRejected();
            return new OpApplyResult(false, CurrentRevision, ex.Message);
        }
        finally
        {
            if (lockAcquired) _lock.Release();
            OpStreamTelemetry.ApplyLatency.RecordElapsedMs(startTimestamp);
        }
    }

    /// <summary>
    /// Adds a peer to the session and returns the current document state.
    /// </summary>
    /// <param name="peerId">The ID of the peer joining.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task representing the join operation, returning the current document state.</returns>
    public Task<DocumentStateResult> JoinAsync(string peerId, CancellationToken ct = default)
    {
        _activePeers.TryAdd(peerId, 0);
        _logger.LogInformation("Peer {PeerId} joined document {DocId} (revision={Revision}, peers={Peers})",
            peerId, DocumentId, CurrentRevision, _activePeers.Count);

        var stateBytes = JsonSerializer.SerializeToUtf8Bytes(_currentState, OpStreamJsonOptions.Default);
        return Task.FromResult(new DocumentStateResult(CurrentRevision, stateBytes, Array.Empty<ReadOnlyMemory<byte>>()));
    }

    /// <summary>
    /// Removes a peer from the session. If no peers remain, takes a snapshot.
    /// </summary>
    /// <param name="peerId">The ID of the peer leaving.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task representing the leave operation.</returns>
    public async Task LeaveAsync(string peerId, CancellationToken ct = default)
    {
        _activePeers.TryRemove(peerId, out _);
        _logger.LogInformation("Peer {PeerId} left document {DocId} (remaining peers={Peers})",
            peerId, DocumentId, _activePeers.Count);

        if (_activePeers.IsEmpty)
        {
            await _opSnapshotter.TakeSnapshotAsync(_currentState, DocumentId, CurrentRevision, OpStreamJsonOptions.Default, ct);
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        OpStreamTelemetry.ActiveDocuments.Add(-1);
        OpStreamEventSource.Log.AdjustActiveDocuments(-1);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task RehydrateOpAsync(StoredOp storedOp)
    {
        await _lock.WaitAsync();
        try
        {
            if (storedOp.Revision <= CurrentRevision)
                return; // Already applied locally or superseded

            var op = JsonSerializer.Deserialize<TOp>(storedOp.Payload.Span, OpStreamJsonOptions.Default)!;
            _currentState = _engine.Apply(_currentState, op);
            CurrentRevision = storedOp.Revision;

            // Re-run post-apply hooks during rehydration so derived state (e.g. comment anchors)
            // stays consistent with the op log even after a cold start or a partial-write crash.
            await RunPostApplyHooksAsync(op, storedOp.AuthorId, isRehydration: true, CancellationToken.None);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<T> ExecuteUnderLockAsync<T>(Func<long, ValueTask<T>> action, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return await action(CurrentRevision);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Invokes every registered <see cref="IPostApplyHook{TOp}"/> and merges their anchor-update
    /// results. Exceptions inside a hook are logged but never propagate — a single misbehaving
    /// side-effect must not prevent the op from being broadcast.
    /// </summary>
    private async Task<IReadOnlyList<AnchorUpdate>?> RunPostApplyHooksAsync(
        TOp appliedOp, string peerId, bool isRehydration, CancellationToken ct)
    {
        if (_postApplyHooks.Count == 0) return null;

        List<AnchorUpdate>? merged = null;
        var ctx = new PostApplyContext<TOp>(DocumentId, CurrentRevision, appliedOp, peerId, isRehydration);

        foreach (var hook in _postApplyHooks)
        {
            try
            {
                var result = await hook.AfterApplyAsync(ctx, ct);
                if (result.AnchorUpdates is { Count: > 0 } updates)
                {
                    merged ??= new List<AnchorUpdate>(updates.Count);
                    merged.AddRange(updates);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Post-apply hook {Hook} failed for doc {DocId} rev {Revision}",
                    hook.GetType().Name, DocumentId, CurrentRevision);
            }
        }
        return merged;
    }
}
