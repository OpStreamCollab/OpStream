using System.Collections.Concurrent;

namespace OpStream.Server.Session;

/// <summary>
/// Tracks which documents each connected peer is currently joined to. Pure in-memory
/// bookkeeping — no I/O, no session lifecycle. Used to fan a disconnect out to every document
/// the peer was editing.
/// </summary>
public interface IPeerRegistry
{
    /// <summary>Records that <paramref name="peerId"/> has joined <paramref name="documentId"/>.</summary>
    void Track(string peerId, string documentId);

    /// <summary>Returns the documents a peer is currently joined to (empty if unknown).</summary>
    string[] DocumentsFor(string peerId);

    /// <summary>Removes the peer entirely and returns the documents it was joined to.</summary>
    IReadOnlyCollection<string> Remove(string peerId);
}

/// <inheritdoc />
public sealed class PeerRegistry : IPeerRegistry
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _peerDocuments = new();

    /// <inheritdoc />
    public void Track(string peerId, string documentId)
    {
        var docs = _peerDocuments.GetOrAdd(peerId, _ => new ConcurrentDictionary<string, byte>());
        docs.TryAdd(documentId, 0);
    }

    /// <inheritdoc />
    public string[] DocumentsFor(string peerId)
        => _peerDocuments.TryGetValue(peerId, out var docs) ? docs.Keys.ToArray() : [];

    /// <inheritdoc />
    public IReadOnlyCollection<string> Remove(string peerId)
        => _peerDocuments.TryRemove(peerId, out var docs) ? docs.Keys.ToArray() : [];
}
