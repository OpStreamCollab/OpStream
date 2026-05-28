using System.Collections.Concurrent;
using OpStream.Shared.Messages.gRPC;
using Grpc.Core;

namespace OpStream.Server.Transports.gRPC;

/// <summary>
/// Manages gRPC client connections and provides broadcasting capabilities for document-based collaboration.
/// </summary>
public class gRPCConnectionManager
{
    private class ConnectionEntry(IServerStreamWriter<ServerMessage> stream)
    {
        public IServerStreamWriter<ServerMessage> Stream { get; } = stream;
        public SemaphoreSlim Lock { get; } = new(1, 1);
    }

    private readonly ConcurrentDictionary<string, ConnectionEntry> _connections = new();
    private readonly ConcurrentDictionary<string, ConcurrentHashSet<string>> _documentGroups = new();

    /// <summary>
    /// Adds a new connection to the manager and associates it with a specific document.
    /// </summary>
    /// <param name="peerId">The unique identifier of the peer.</param>
    /// <param name="documentId">The identifier of the document the peer is joining.</param>
    /// <param name="stream">The gRPC response stream for the peer.</param>
    public void AddConnection(string peerId, string documentId, IServerStreamWriter<ServerMessage> stream)
    {
        _connections[peerId] = new ConnectionEntry(stream);
        var group = _documentGroups.GetOrAdd(documentId, _ => new ConcurrentHashSet<string>());
        group.Add(peerId);
    }

    /// <summary>
    /// Removes a connection and its association with any documents.
    /// </summary>
    /// <param name="peerId">The unique identifier of the peer to remove.</param>
    public void RemoveConnection(string peerId)
    {
        if (_connections.TryRemove(peerId, out var entry))
        {
            entry.Lock.Dispose();
        }
        foreach (var group in _documentGroups.Values)
        {
            group.Remove(peerId);
        }
    }

    /// <summary>
    /// Sends a message to a specific peer in a thread-safe manner.
    /// </summary>
    /// <param name="peerId">The unique identifier of the target peer.</param>
    /// <param name="message">The message to send.</param>
    public async Task SendToPeerAsync(string peerId, ServerMessage message)
    {
        if (_connections.TryGetValue(peerId, out var entry))
        {
            await entry.Lock.WaitAsync();
            try
            {
                await entry.Stream.WriteAsync(message);
            }
            catch
            {
                RemoveConnection(peerId);
            }
            finally
            {
                entry.Lock.Release();
            }
        }
    }

    /// <summary>
    /// Broadcasts a message to all peers connected to a specific document, optionally excluding one peer.
    /// </summary>
    /// <param name="documentId">The identifier of the document.</param>
    /// <param name="message">The message to broadcast.</param>
    /// <param name="excludePeerId">Optional peer identifier to exclude from the broadcast (usually the sender).</param>
    public async Task BroadcastToDocumentAsync(string documentId, ServerMessage message, string? excludePeerId = null)
    {
        if (_documentGroups.TryGetValue(documentId, out var peers))
        {
            var tasks = new List<Task>();
            foreach (var peerId in peers)
            {
                if (peerId == excludePeerId) continue;
                tasks.Add(SendToPeerAsync(peerId, message));
            }
            await Task.WhenAll(tasks);
        }
    }
}

/// <summary>
/// A thread-safe hash set implementation based on ConcurrentDictionary.
/// </summary>
/// <typeparam name="T">The type of elements in the set.</typeparam>
public class ConcurrentHashSet<T> where T : notnull
{
    private readonly ConcurrentDictionary<T, byte> _dictionary = new();

    /// <summary>
    /// Adds an item to the set.
    /// </summary>
    /// <param name="item">The item to add.</param>
    /// <returns>True if the item was added; false if it already existed.</returns>
    public bool Add(T item) => _dictionary.TryAdd(item, 0);

    /// <summary>
    /// Removes an item from the set.
    /// </summary>
    /// <param name="item">The item to remove.</param>
    /// <returns>True if the item was removed; false if it was not found.</returns>
    public bool Remove(T item) => _dictionary.TryRemove(item, out _);

    /// <summary>
    /// Returns an enumerator that iterates through the set.
    /// </summary>
    public IEnumerator<T> GetEnumerator() => _dictionary.Keys.GetEnumerator();
}
