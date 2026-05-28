using OpStream.Constants;
using OpStream.Shared.Messages;
using System.Text.Json;

namespace OpStream.Server.Session;

/// <summary>
/// Thin typed facade over <see cref="IAwarenessSession"/>: callers work with a
/// domain POCO (e.g. <c>CursorPresence</c>, <c>SelectionPresence</c>) and the
/// facade handles the round-trip through <see cref="JsonElement"/>.
/// <para>
/// Deliberately a wrapper — the core engine remains untyped, so all transports,
/// the cluster wire format, and existing client code keep working unchanged.
/// </para>
/// </summary>
public sealed class TypedAwarenessSession<TPresence>
{
    private readonly IAwarenessSession _inner;
    private readonly JsonSerializerOptions _jsonOptions;

    public TypedAwarenessSession(IAwarenessSession inner, JsonSerializerOptions? jsonOptions = null)
    {
        _inner = inner;
        _jsonOptions = jsonOptions ?? OpStreamJsonOptions.Default;
    }

    public string DocumentId => _inner.DocumentId;

    /// <summary>Publishes the typed presence for the given peer.</summary>
    public Task<AwarenessState> UpdateAsync(string peerId, TPresence presence, CancellationToken ct = default)
    {
        var element = JsonSerializer.SerializeToElement(presence, _jsonOptions);
        return _inner.UpdateAsync(peerId, element, ct);
    }

    /// <summary>Returns every live peer's presence decoded back into <typeparamref name="TPresence"/>.</summary>
    public IReadOnlyList<(string PeerId, TPresence? Presence, DateTimeOffset LastUpdated)> GetStates()
    {
        var raw = _inner.GetStates();
        var result = new List<(string, TPresence?, DateTimeOffset)>(raw.Count);
        foreach (var s in raw)
        {
            var typed = s.Data.Deserialize<TPresence>(_jsonOptions);
            result.Add((s.PeerId, typed, s.LastUpdated));
        }
        return result;
    }

    public Task LeaveAsync(string peerId, CancellationToken ct = default) => _inner.LeaveAsync(peerId, ct);
}
