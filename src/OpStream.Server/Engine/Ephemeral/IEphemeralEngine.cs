namespace OpStream.Server.Engine.Ephemeral;

/// <summary>
/// Pure contract for engines that manage <b>ephemeral</b> state — i.e. data that
/// is broadcast in real time but never persisted to the op log
/// (presence/cursors, undo stacks per peer, live preview overlays, etc.).
/// <para>
/// Counterpart of <see cref="IOpEngine{TDoc, TOp}"/> for the non-persisted side
/// of the system. Implementations must be <b>pure</b>: no I/O, no side effects,
/// no clock reads — the clock is passed in for testability.
/// </para>
/// </summary>
/// <typeparam name="TState">The per-peer state managed by the engine.</typeparam>
public interface IEphemeralEngine<TState>
{
    /// <summary>
    /// Combines an incoming update with the current state for the same peer.
    /// Default semantics are last-writer-wins; alternative engines may merge field-wise
    /// or append to a bounded buffer.
    /// </summary>
    TState Merge(TState? existing, TState incoming);

    /// <summary>
    /// Determines whether a stored state should be evicted because it is too old.
    /// </summary>
    bool IsExpired(TState state, DateTimeOffset now);

    /// <summary>
    /// Determines whether <paramref name="incoming"/> would have no observable effect
    /// over <paramref name="existing"/>, allowing the channel layer to skip the broadcast.
    /// Cheap coalescing of redundant cursor pings, for example.
    /// </summary>
    bool IsNoOp(TState? existing, TState incoming);
}
