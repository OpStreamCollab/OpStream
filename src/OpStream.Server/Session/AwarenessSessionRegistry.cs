using System.Collections.Concurrent;
using OpStream.Shared.Abstractions;

namespace OpStream.Server.Session;

/// <summary>
/// Owns the map of live <see cref="IAwarenessSession"/> instances (presence / cursors) on this
/// node. Awareness is ephemeral, so this registry has no persistence concerns — it only creates,
/// hands out, and disposes per-document presence sessions.
/// </summary>
public interface IAwarenessSessionRegistry
{
    /// <summary>Returns the awareness session for a document, creating it on first use.</summary>
    Task<IAwarenessSession> GetOrCreateAsync(string documentId, CancellationToken ct = default);

    /// <summary>Returns the awareness session if one exists; does not create one.</summary>
    IAwarenessSession? TryGet(string documentId);

    /// <summary>Disposes and removes the awareness session for the document. Safe if absent.</summary>
    Task CloseAsync(string documentId);
}

/// <inheritdoc />
public sealed class AwarenessSessionRegistry(IBackplane backplane, IDocumentLockRegistry locks)
    : IAwarenessSessionRegistry
{
    private readonly ConcurrentDictionary<string, IAwarenessSession> _awareness = new();

    /// <inheritdoc />
    public IAwarenessSession? TryGet(string documentId)
        => _awareness.TryGetValue(documentId, out var session) ? session : null;

    /// <inheritdoc />
    public async Task<IAwarenessSession> GetOrCreateAsync(string documentId, CancellationToken ct = default)
    {
        if (_awareness.TryGetValue(documentId, out var existing))
            return existing;

        using (await locks.AcquireAsync(documentId, ct))
        {
            if (_awareness.TryGetValue(documentId, out var session))
                return session;

            var newSession = AwarenessSession.CreateDefault(documentId, backplane);
            _awareness.TryAdd(documentId, newSession);
            return newSession;
        }
    }

    /// <inheritdoc />
    public async Task CloseAsync(string documentId)
    {
        if (_awareness.TryRemove(documentId, out var session))
            await session.DisposeAsync();
    }
}
