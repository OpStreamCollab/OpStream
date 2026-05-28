using OpStream.Server.Engine.Ephemeral;
using OpStream.Server.Models;

namespace OpStream.Server.Engine.UndoRedo;

/// <summary>
/// Default <see cref="IUndoRedoEngine{TDoc,TOp}"/>. Composes an
/// <see cref="IOpEngine{TDoc,TOp}"/> to do the heavy lifting (Invert + Transform) and
/// reuses <see cref="IPeerStateStore{TState}"/> from the Ephemeral primitives for the
/// per-peer stacks.
/// </summary>
public sealed class UndoRedoEngine<TDoc, TOp> : IUndoRedoEngine<TDoc, TOp>
{
    private readonly IOpEngine<TDoc, TOp> _engine;
    private readonly UndoRedoOptions _options;
    private readonly IPeerStateStore<PeerUndoState> _peers;

    // Shared op log. Indexed by Sequence (NOT by list position — we may drop the head when
    // MaxHistoryDepth is exceeded, and per-peer stacks must keep referencing the same ids).
    private readonly SortedDictionary<long, RecordedOp<TOp>> _log = new();
    private long _nextSequence = 1;

    // All mutation paths happen serialized by the caller (DocumentSession's apply lock),
    // but undo/redo prepare paths may interleave with another peer's record. Single lock
    // keeps the log + stacks coherent without imposing per-stack locking.
    private readonly object _gate = new();

    public UndoRedoEngine(IOpEngine<TDoc, TOp> engine, UndoRedoOptions? options = null, IPeerStateStore<PeerUndoState>? peerStore = null)
    {
        _engine = engine;
        _options = options ?? new UndoRedoOptions();
        _peers = peerStore ?? new PeerStateStore<PeerUndoState>();
    }

    public long RecordApplied(string peerId, TOp op, TDoc preState, long revision)
    {
        var inverse = _engine.Invert(op, preState);

        lock (_gate)
        {
            var sequence = _nextSequence++;
            _log.Add(sequence, new RecordedOp<TOp>(sequence, peerId, op, inverse, revision));

            var peer = GetOrCreate(peerId);
            peer.UndoSequences.Push(sequence);
            if (_options.ClearRedoOnNewOp) peer.RedoSequences.Clear();

            TrimLog();
            return sequence;
        }
    }

    public UndoRedoPreparation<TOp> PrepareUndo(string peerId, TDoc currentState)
    {
        lock (_gate)
        {
            if (!_peers.TryGet(peerId, out var peer) || peer is null) return UndoRedoPreparation<TOp>.Nothing;
            return PrepareFromStack(peer.UndoSequences, useInverse: true, currentState);
        }
    }

    public UndoRedoPreparation<TOp> PrepareRedo(string peerId, TDoc currentState)
    {
        lock (_gate)
        {
            if (!_peers.TryGet(peerId, out var peer) || peer is null) return UndoRedoPreparation<TOp>.Nothing;
            // Redo means re-applying what undo undid. The redo stack stores the *undo* entries,
            // so the forward op to re-apply is the inverse-of-the-inverse — i.e. the original Op.
            return PrepareFromStack(peer.RedoSequences, useInverse: false, currentState);
        }
    }

    public void NotifyUndoApplied(string peerId, long consumedSequence)
    {
        lock (_gate)
        {
            if (!_peers.TryGet(peerId, out var peer) || peer is null) return;
            if (!RemoveFromStack(peer.UndoSequences, consumedSequence)) return;
            peer.RedoSequences.Push(consumedSequence);
        }
    }

    public void NotifyRedoApplied(string peerId, long consumedSequence)
    {
        lock (_gate)
        {
            if (!_peers.TryGet(peerId, out var peer) || peer is null) return;
            if (!RemoveFromStack(peer.RedoSequences, consumedSequence)) return;
            peer.UndoSequences.Push(consumedSequence);
        }
    }

    /// <summary>
    /// Removes the first occurrence of <paramref name="value"/> from the stack while
    /// preserving the order of all other entries. O(n) but n is bounded by
    /// <see cref="UndoRedoOptions.MaxHistoryDepth"/>.
    /// </summary>
    private static bool RemoveFromStack(Stack<long> stack, long value)
    {
        if (stack.Count == 0) return false;
        var topFirst = stack.ToArray();
        int idx = Array.IndexOf(topFirst, value);
        if (idx < 0) return false;

        stack.Clear();
        // ToArray() returns top-first; to rebuild we push bottom-first, skipping idx.
        for (int i = topFirst.Length - 1; i >= 0; i--)
        {
            if (i == idx) continue;
            stack.Push(topFirst[i]);
        }
        return true;
    }

    public bool CanUndo(string peerId)
    {
        lock (_gate)
        {
            return _peers.TryGet(peerId, out var p) && p is not null && p.UndoSequences.Count > 0;
        }
    }

    public bool CanRedo(string peerId)
    {
        lock (_gate)
        {
            return _peers.TryGet(peerId, out var p) && p is not null && p.RedoSequences.Count > 0;
        }
    }

    // ── Internals ────────────────────────────────────────────────────────────────────

    private UndoRedoPreparation<TOp> PrepareFromStack(Stack<long> stack, bool useInverse, TDoc currentState)
    {
        // Walk down the stack until we find a candidate that survives the rebase.
        // We don't mutate the stack here — NotifyUndoApplied/NotifyRedoApplied do that
        // once the inverse op is actually accepted by the session.
        var snapshot = stack.ToArray(); // top-of-stack first
        foreach (var seq in snapshot)
        {
            if (!_log.TryGetValue(seq, out var recorded)) continue; // dropped by TrimLog

            var candidate = useInverse ? recorded.Inverse : recorded.Op;

            // Rebase against every op recorded strictly after this one — including the
            // user's own subsequent ops, which is intentional: undoing op N must yield
            // to anything that happened after N in the global order.
            foreach (var kvp in _log)
            {
                if (kvp.Key <= seq) continue;
                var transformed = _engine.Transform(candidate, kvp.Value.Op, TransformPriority.ExistingWins);
                if (transformed is null || _engine.IsNoOp(transformed))
                {
                    candidate = default!;
                    break;
                }
                candidate = transformed;
            }

            if (candidate is not null && !_engine.IsNoOp(candidate))
            {
                // Final step: bump LWW timestamps so the candidate beats every existing
                // register in currentState. Identity for non-LWW engines (default impl).
                candidate = _engine.RestampToWin(candidate, currentState);
                return UndoRedoPreparation<TOp>.Ready(candidate, seq);
            }
            // Otherwise the entry has been fully absorbed by concurrent edits.
            // We don't pop it here; the caller is expected to call NotifyUndoApplied only on
            // success. Returning Nothing for now is the correct UX (greyed-out undo button).
            // A future iteration may add an Eager mode that prunes nullified entries.
        }
        return UndoRedoPreparation<TOp>.Nothing;
    }

    private PeerUndoState GetOrCreate(string peerId)
    {
        if (_peers.TryGet(peerId, out var existing) && existing is not null) return existing;
        var fresh = new PeerUndoState();
        _peers.Upsert(peerId, fresh);
        return fresh;
    }

    private void TrimLog()
    {
        while (_log.Count > _options.MaxHistoryDepth)
        {
            var oldestKey = _log.Keys.First();
            _log.Remove(oldestKey);
            // Per-peer stacks may still reference oldestKey — PrepareFromStack tolerates that
            // by skipping log entries that are gone (TryGetValue miss).
        }
    }
}
