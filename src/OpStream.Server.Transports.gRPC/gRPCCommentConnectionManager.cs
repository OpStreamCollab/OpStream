using Grpc.Core;
using OpStream.Shared.Messages.gRPC;
using System.Collections.Concurrent;

namespace OpStream.Server.Transports.gRPC;

/// <summary>
/// Tracks active <c>SubscribeComments</c> server-streaming connections keyed by document id.
/// </summary>
public sealed class gRPCCommentConnectionManager
{
    // documentId → list of active writers
    private readonly ConcurrentDictionary<string, List<IServerStreamWriter<CommentEvent>>> _writers =
        new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public void Register(string documentId, IServerStreamWriter<CommentEvent> writer)
    {
        lock (_lock)
        {
            if (!_writers.TryGetValue(documentId, out var list))
            {
                list = new List<IServerStreamWriter<CommentEvent>>();
                _writers[documentId] = list;
            }
            list.Add(writer);
        }
    }

    public void Unregister(string documentId, IServerStreamWriter<CommentEvent> writer)
    {
        lock (_lock)
        {
            if (_writers.TryGetValue(documentId, out var list))
            {
                list.Remove(writer);
                if (list.Count == 0) _writers.TryRemove(documentId, out _);
            }
        }
    }

    public async Task BroadcastAsync(string documentId, CommentEvent evt)
    {
        List<IServerStreamWriter<CommentEvent>> snapshot;
        lock (_lock)
        {
            if (!_writers.TryGetValue(documentId, out var list) || list.Count == 0) return;
            snapshot = new List<IServerStreamWriter<CommentEvent>>(list);
        }

        foreach (var writer in snapshot)
        {
            try { await writer.WriteAsync(evt); }
            catch { /* connection dropped — will be unregistered when the call completes */ }
        }
    }
}
