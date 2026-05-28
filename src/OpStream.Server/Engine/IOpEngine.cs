using OpStream.Server.Models;

namespace OpStream.Server.Engine;

/// <summary>
/// Mathematical contract for applying and transforming operations.
/// All implementations must be pure (no side effects, no I/O).
/// </summary>
/// <typeparam name="TDoc">The strongly typed document state.</typeparam>
/// <typeparam name="TOp">The strongly typed operation.</typeparam>
public interface IOpEngine<TDoc, TOp>
{
    /// <summary>
    /// Applies an operation to the current state, returning a new mutated state.
    /// </summary>
    TDoc Apply(TDoc state, TOp op);

    /// <summary>
    /// Transforms an incoming operation against an existing concurrent operation 
    /// so its original intent remains valid in the new state.
    /// </summary>
    /// <returns>The transformed operation, or null if the operation is nullified by the existing one.</returns>
    TOp? Transform(TOp incoming, TOp existing, TransformPriority priority);

    /// <summary>
    /// Combines two sequential operations into a single, more efficient one, if supported by the engine.
    /// </summary>
    /// <returns>The combined operation, or null if they are not composable.</returns>
    TOp? Compose(TOp a, TOp b);

    /// <summary>
    /// Generates the inverse operation for Undo systems.
    /// </summary>
    TOp Invert(TOp op, TDoc preState);

    /// <summary>
    /// Determines whether an operation has no actual effect on the document (e.g., a retain of length 0).
    /// </summary>
    bool IsNoOp(TOp op);

    /// <summary>
    /// Returns a variant of <paramref name="op"/> that is guaranteed to win against the
    /// current contents of <paramref name="currentState"/> when applied — typically by
    /// rewriting its LWW timestamps to a value strictly greater than every timestamp
    /// already present in the document.
    /// <para>
    /// Required by <c>UndoRedoEngine</c>: cached inverses carry the timestamps they had
    /// at record time, which may have been overtaken by concurrent writes by the time the
    /// user clicks Undo. Engines that don't use timestamp-based LWW (OT engines, move-log
    /// CRDTs) inherit the default identity implementation and are unaffected.
    /// </para>
    /// </summary>
    TOp RestampToWin(TOp op, TDoc currentState) => op;
}
