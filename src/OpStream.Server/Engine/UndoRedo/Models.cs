namespace OpStream.Server.Engine.UndoRedo;

/// <summary>
/// One entry in the engine's shared op log.
/// The inverse is precomputed at record-time so we never need to snapshot the document
/// state (would otherwise be expensive for non-trivial engines).
/// </summary>
/// <param name="Sequence">Monotonic id assigned by the engine when the op is recorded.</param>
/// <param name="PeerId">The author of the original op.</param>
/// <param name="Op">The original forward op as it was applied.</param>
/// <param name="Inverse">The inverse of <paramref name="Op"/> computed against the pre-apply state.</param>
/// <param name="Revision">The document revision that contains this op.</param>
public record RecordedOp<TOp>(long Sequence, string PeerId, TOp Op, TOp Inverse, long Revision);

/// <summary>
/// Per-peer undo / redo state. Stacks hold <see cref="RecordedOp{TOp}.Sequence"/> ids
/// referring into the shared log.
/// </summary>
public sealed class PeerUndoState
{
    public Stack<long> UndoSequences { get; } = new();
    public Stack<long> RedoSequences { get; } = new();
}

/// <summary>
/// Outcome of <see cref="IUndoRedoEngine{TDoc,TOp}.PrepareUndo"/> / <c>PrepareRedo</c>.
/// <see cref="ConsumedSequence"/> identifies which log entry the candidate originated
/// from; callers must echo it back via <c>NotifyUndoApplied</c> / <c>NotifyRedoApplied</c>
/// so the engine removes the right entry from the per-peer stack instead of blindly
/// popping the top (which may differ when shallower entries were nullified by Transform).
/// </summary>
public readonly record struct UndoRedoPreparation<TOp>(bool HasOp, TOp? Op, long ConsumedSequence)
{
    public static UndoRedoPreparation<TOp> Nothing => new(false, default, 0);
    public static UndoRedoPreparation<TOp> Ready(TOp op, long consumedSequence) => new(true, op, consumedSequence);
}

/// <summary>
/// Tunables for the per-peer undo / redo behavior.
/// </summary>
public sealed class UndoRedoOptions
{
    /// <summary>
    /// Maximum number of recorded ops kept in the shared log. Once exceeded, the
    /// oldest entries are dropped and any per-peer stack reference to them is purged.
    /// Defaults to 500.
    /// </summary>
    public int MaxHistoryDepth { get; init; } = 500;

    /// <summary>
    /// When <c>true</c>, recording an op by a peer clears that peer's redo stack
    /// (the standard editor behavior: a new action invalidates the redo branch).
    /// </summary>
    public bool ClearRedoOnNewOp { get; init; } = true;
}
