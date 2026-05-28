using OpStream.Constants;
using OpStream.Server.Engine.Awareness;
using OpStream.Server.Engine.Ephemeral;
using OpStream.Shared.Abstractions;
using OpStream.Shared.Messages;
using System.Text.Json;

namespace OpStream.Server.Session;

/// <summary>
/// Per-document presence session: tracks each peer's awareness state, broadcasts
/// updates across the cluster, and evicts stale peers.
/// <para>
/// Composed of three single-responsibility collaborators:
/// <see cref="IEphemeralEngine{TState}"/> (merge / expiry / coalescing policy),
/// <see cref="IPeerStateStore{TState}"/> (in-memory per-peer storage), and
/// <see cref="IEphemeralChannel{TState}"/> (cluster-wide transport).
/// Same shape future engines (HistoryEngine, CanvasCrdtEngine presence, …) will reuse.
/// </para>
/// </summary>
public interface IAwarenessSession : IAsyncDisposable
{
    /// <summary>Document this session belongs to.</summary>
    string DocumentId { get; }

    /// <summary>
    /// Records or updates a peer's untyped awareness payload, broadcasts the change,
    /// and returns the newly stored <see cref="AwarenessState"/>.
    /// </summary>
    /// <param name="peerId">The ID of the peer updating their awareness.</param>
    /// <param name="data">The awareness data.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task representing the update operation, returning the updated awareness state.</returns>
    Task<AwarenessState> UpdateAsync(string peerId, JsonElement data, CancellationToken ct = default);

    /// <summary>
    /// Returns all currently live (non-expired) peer states. Expired entries are
    /// evicted as a side effect of this call.
    /// </summary>
    /// <returns>A read-only list of current awareness states.</returns>
    IReadOnlyList<AwarenessState> GetStates();

    /// <summary>
    /// Removes a peer's state, e.g. on disconnect.
    /// </summary>
    /// <param name="peerId">The ID of the peer leaving.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task representing the leave operation.</returns>
    Task LeaveAsync(string peerId, CancellationToken ct = default);
}

/// <inheritdoc cref="IAwarenessSession"/>
public sealed class AwarenessSession : IAwarenessSession
{
    private readonly IEphemeralEngine<AwarenessState> _engine;
    private readonly IPeerStateStore<AwarenessState> _store;
    private readonly IEphemeralChannel<AwarenessState> _channel;
    private readonly TimeProvider _clock;

    /// <inheritdoc/>
    public string DocumentId { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="AwarenessSession"/> class.
    /// </summary>
    /// <param name="documentId">The ID of the document.</param>
    /// <param name="engine">The ephemeral engine for awareness.</param>
    /// <param name="store">The store for peer states.</param>
    /// <param name="channel">The channel for cluster-wide communication.</param>
    /// <param name="clock">The time provider.</param>
    public AwarenessSession(
        string documentId,
        IEphemeralEngine<AwarenessState> engine,
        IPeerStateStore<AwarenessState> store,
        IEphemeralChannel<AwarenessState> channel,
        TimeProvider? clock = null)
    {
        DocumentId = documentId;
        _engine = engine;
        _store = store;
        _channel = channel;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Convenience factory mirroring the previous <c>new AwarenessManager(docId, backplane)</c>
    /// call site — wires the default engine/store/channel against the supplied backplane.
    /// </summary>
    /// <param name="documentId">The ID of the document.</param>
    /// <param name="backplane">The backplane instance.</param>
    /// <param name="options">The awareness options.</param>
    /// <param name="clock">The time provider.</param>
    /// <returns>A new instance of <see cref="AwarenessSession"/>.</returns>
    public static AwarenessSession CreateDefault(string documentId, IBackplane backplane, AwarenessOptions? options = null, TimeProvider? clock = null)
    {
        var engine = new AwarenessEngine(options);
        var store = new PeerStateStore<AwarenessState>();
        var channel = new BackplaneEphemeralChannel<AwarenessState>(backplane, OpStreamConstants.BackplaneMessages.ReceiveAwarenessUpdate);
        return new AwarenessSession(documentId, engine, store, channel, clock);
    }

    /// <inheritdoc/>
    public async Task<AwarenessState> UpdateAsync(string peerId, JsonElement data, CancellationToken ct = default)
    {
        var incoming = new AwarenessState(peerId, data, _clock.GetUtcNow());

        _store.TryGet(peerId, out var existing);
        if (_engine.IsNoOp(existing, incoming))
        {
            return existing!;
        }

        var merged = _engine.Merge(existing, incoming);
        _store.Upsert(peerId, merged);

        await _channel.PublishAsync(DocumentId, peerId, merged, ct);
        return merged;
    }

    /// <inheritdoc/>
    public IReadOnlyList<AwarenessState> GetStates()
    {
        var now = _clock.GetUtcNow();
        _store.EvictWhere(s => _engine.IsExpired(s, now));

        var snapshot = _store.Snapshot();
        var result = new List<AwarenessState>(snapshot.Count);
        foreach (var kvp in snapshot) result.Add(kvp.Value);
        return result;
    }

    /// <inheritdoc/>
    public Task LeaveAsync(string peerId, CancellationToken ct = default)
    {
        _store.Remove(peerId);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _store.Clear();
        return ValueTask.CompletedTask;
    }
}
