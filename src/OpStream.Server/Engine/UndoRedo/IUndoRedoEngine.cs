namespace OpStream.Server.Engine.UndoRedo;

/// <summary>
/// Transversal engine that maintains per-peer undo / redo stacks on top of any
/// <see cref="IOpEngine{TDoc,TOp}"/>. Pure runtime state — nothing is persisted, the
/// stacks live only for the lifetime of the document session.
/// <para>
/// Lifecycle:
/// <list type="number">
///   <item><see cref="RecordApplied"/> is called by the document session for every op it accepts.</item>
///   <item>When a peer hits Undo, the host calls <see cref="PrepareUndo"/>. The engine returns
///         the inverse op already transformed against every concurrent op by others — ready
///         to flow through the normal <c>ApplyOpAsync</c> pipeline.</item>
///   <item>Once that inverse op is itself accepted, <see cref="NotifyUndoApplied"/> moves the
///         entry from the peer's undo stack to its redo stack.</item>
///   <item>Redo is symmetric.</item>
/// </list>
/// </para>
/// </summary>
public interface IUndoRedoEngine<TDoc, TOp>
{
    /// <summary>
    /// Registers that <paramref name="op"/> by <paramref name="peerId"/> has just been applied,
    /// taking <paramref name="preState"/> from the snapshot before the op so the inverse can be
    /// computed once and cached. Returns the assigned sequence id for diagnostic / testing use.
    /// </summary>
    long RecordApplied(string peerId, TOp op, TDoc preState, long revision);

    /// <summary>
    /// Builds the next undo op for <paramref name="peerId"/>: the inverse of their most recent
    /// recorded op, rebased over every op recorded after it (including from other peers) and
    /// finally re-stamped via <see cref="IOpEngine{TDoc,TOp}.RestampToWin"/> so LWW-CRDT
    /// engines are guaranteed to win at Apply time.
    /// Returns <see cref="UndoRedoPreparation{TOp}.Nothing"/> if there's nothing to undo or every
    /// candidate has been fully nullified by concurrent changes.
    /// </summary>
    /// <param name="peerId">The peer requesting the undo.</param>
    /// <param name="currentState">
    /// The post-state of the document at the moment of the request. Passed to
    /// <c>RestampToWin</c> so the prepared op can outrank every existing LWW timestamp.
    /// </param>
    UndoRedoPreparation<TOp> PrepareUndo(string peerId, TDoc currentState);

    /// <summary>Builds the next redo op for <paramref name="peerId"/>. Symmetric to <see cref="PrepareUndo"/>.</summary>
    UndoRedoPreparation<TOp> PrepareRedo(string peerId, TDoc currentState);

    /// <summary>
    /// Notifies the engine that the previously prepared undo op has been accepted. The caller
    /// must pass back the <see cref="UndoRedoPreparation{TOp}.ConsumedSequence"/> returned by
    /// <see cref="PrepareUndo"/> so the right entry is moved from the undo stack to the redo
    /// stack — necessary because <see cref="PrepareUndo"/> may have walked past nullified
    /// entries on top of the consumed one.
    /// </summary>
    void NotifyUndoApplied(string peerId, long consumedSequence);

    /// <summary>Symmetric to <see cref="NotifyUndoApplied"/>.</summary>
    void NotifyRedoApplied(string peerId, long consumedSequence);

    bool CanUndo(string peerId);
    bool CanRedo(string peerId);
}
