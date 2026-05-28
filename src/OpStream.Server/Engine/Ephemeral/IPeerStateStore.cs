namespace OpStream.Server.Engine.Ephemeral;

/// <summary>
/// Concurrent, in-memory storage of one <typeparamref name="TState"/> per peer.
/// Pure data plane — no merge, expiry, or transport logic; those belong to
/// <see cref="IEphemeralEngine{TState}"/> and <see cref="IEphemeralChannel{TState}"/>.
/// </summary>
/// <typeparam name="TState">The per-peer state value.</typeparam>
public interface IPeerStateStore<TState>
{
    /// <summary>Number of distinct peers currently tracked.</summary>
    int Count { get; }

    /// <summary>
    /// Inserts or replaces the state for the given peer. Returns the value now stored.
    /// </summary>
    TState Upsert(string peerId, TState state);

    /// <summary>
    /// Reads the state for the given peer if present.
    /// </summary>
    bool TryGet(string peerId, out TState? state);

    /// <summary>
    /// Removes the state for the given peer, if any. Returns whether a value was removed.
    /// </summary>
    bool Remove(string peerId);

    /// <summary>
    /// Returns a point-in-time snapshot of every peer's state.
    /// </summary>
    IReadOnlyList<KeyValuePair<string, TState>> Snapshot();

    /// <summary>
    /// Removes every entry matching <paramref name="predicate"/>. Returns how many were removed.
    /// </summary>
    int EvictWhere(Func<TState, bool> predicate);

    /// <summary>
    /// Clears all entries.
    /// </summary>
    void Clear();
}
