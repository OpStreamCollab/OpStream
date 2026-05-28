using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OpStream.Shared.Messages;

namespace OpStream.Server.Transports.WebSockets;

/// <summary>
/// Manages active WebSocket connections and their association with documents.
/// </summary>
public class WebSocketConnectionManager
{
    private class ConnectionEntry(WebSocket webSocket)
    {
        public WebSocket WebSocket { get; } = webSocket;
        public SemaphoreSlim Lock { get; } = new(1, 1);
    }

    private readonly ConcurrentDictionary<string, ConnectionEntry> _connections = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _documentGroups = new();
    
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Adds a new WebSocket connection for a peer and associates it with a document.
    /// </summary>
    /// <param name="peerId">The ID of the peer.</param>
    /// <param name="documentId">The ID of the document.</param>
    /// <param name="webSocket">The WebSocket connection.</param>
    public void AddConnection(string peerId, string documentId, WebSocket webSocket)
    {
        _connections[peerId] = new ConnectionEntry(webSocket);
        var group = _documentGroups.GetOrAdd(documentId, _ => new ConcurrentDictionary<string, byte>());
        group.TryAdd(peerId, 0);
    }

    /// <summary>
    /// Removes a WebSocket connection for a peer.
    /// </summary>
    /// <param name="peerId">The ID of the peer to remove.</param>
    public void RemoveConnection(string peerId)
    {
        if (_connections.TryRemove(peerId, out var entry))
        {
            entry.Lock.Dispose();
        }
        foreach (var group in _documentGroups.Values)
        {
            group.TryRemove(peerId, out _);
        }
    }

    /// <summary>
    /// Sends a WebSocket message to a specific peer.
    /// </summary>
    /// <param name="peerId">The ID of the peer to send the message to.</param>
    /// <param name="message">The message to send.</param>
    public async Task SendToPeerAsync(string peerId, WebSocketMessage message)
    {
        if (_connections.TryGetValue(peerId, out var entry))
        {
            if (entry.WebSocket.State != WebSocketState.Open)
            {
                RemoveConnection(peerId);
                return;
            }

            await entry.Lock.WaitAsync();
            try
            {
                var json = JsonSerializer.Serialize(message, JsonOptions);
                var bytes = Encoding.UTF8.GetBytes(json);
                await entry.WebSocket.SendAsync(new ArraySegment<byte>(bytes), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
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
    /// Broadcasts a WebSocket message to all peers associated with a document.
    /// </summary>
    /// <param name="documentId">The ID of the document.</param>
    /// <param name="message">The message to broadcast.</param>
    /// <param name="excludePeerId">An optional peer ID to exclude from the broadcast.</param>
    public async Task BroadcastToDocumentAsync(string documentId, WebSocketMessage message, string? excludePeerId = null)
    {
        if (_documentGroups.TryGetValue(documentId, out var peers))
        {
            var tasks = new List<Task>();
            foreach (var peerId in peers.Keys)
            {
                if (peerId == excludePeerId) continue;
                tasks.Add(SendToPeerAsync(peerId, message));
            }
            await Task.WhenAll(tasks);
        }
    }
}
