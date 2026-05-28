using System.Collections.Concurrent;

namespace OpStream.Server.Engine.Ephemeral;

/// <summary>
/// Default <see cref="IPeerStateStore{TState}"/> backed by a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </summary>
public sealed class PeerStateStore<TState> : IPeerStateStore<TState>
{
    private readonly ConcurrentDictionary<string, TState> _states = new();

    public int Count => _states.Count;

    public TState Upsert(string peerId, TState state)
    {
        _states[peerId] = state;
        return state;
    }

    public bool TryGet(string peerId, out TState? state)
    {
        if (_states.TryGetValue(peerId, out var value))
        {
            state = value;
            return true;
        }
        state = default;
        return false;
    }

    public bool Remove(string peerId) => _states.TryRemove(peerId, out _);

    public IReadOnlyList<KeyValuePair<string, TState>> Snapshot() => _states.ToArray();

    public int EvictWhere(Func<TState, bool> predicate)
    {
        var evicted = 0;
        foreach (var kvp in _states)
        {
            if (predicate(kvp.Value) && _states.TryRemove(kvp))
            {
                evicted++;
            }
        }
        return evicted;
    }

    public void Clear() => _states.Clear();
}
