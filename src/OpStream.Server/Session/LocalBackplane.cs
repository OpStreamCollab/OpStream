using System.Collections.Concurrent;
using System.Collections.Immutable;
using OpStream.Shared.Abstractions;

namespace OpStream.Server.Session;

/// <summary>
/// In-process <see cref="IBackplane"/> implementation for single-node deployments.
/// <para>
/// Delivers <see cref="PublishAsync"/> messages directly to every local subscriber
/// of the same document, so peers connected to the same node receive each other's
/// updates without needing an external transport (Redis, NATS, …).
/// </para>
/// <para>
/// <see cref="SendRequestAsync"/> dispatches in-process when the target is this node
/// and fails otherwise — by design, this implementation cannot reach other nodes.
/// </para>
/// </summary>
public sealed class LocalBackplane : IBackplane
{
    private readonly ConcurrentDictionary<string, ImmutableList<Func<BackplaneMessage, ValueTask>>> _subscribers = new();
    private Func<BackplaneRequest, Task<BackplaneResponse>>? _requestHandler;

    public string NodeId { get; } = "LocalNode";

    public Task<IAsyncDisposable> SubscribeAsync(string documentId, Func<BackplaneMessage, ValueTask> handler, CancellationToken ct = default)
    {
        _subscribers.AddOrUpdate(
            documentId,
            _ => ImmutableList.Create(handler),
            (_, list) => list.Add(handler));

        return Task.FromResult<IAsyncDisposable>(new Subscription(this, documentId, handler));
    }

    public async Task PublishAsync(string documentId, BackplaneMessage message, CancellationToken ct = default)
    {
        if (!_subscribers.TryGetValue(documentId, out var handlers))
        {
            return;
        }

        foreach (var handler in handlers)
        {
            await handler(message);
        }
    }

    public async Task<BackplaneResponse> SendRequestAsync(string targetNodeId, string type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (targetNodeId != NodeId)
        {
            return new BackplaneResponse(
                string.Empty,
                false,
                ReadOnlyMemory<byte>.Empty,
                $"LocalBackplane cannot reach remote node '{targetNodeId}'. Configure a real backplane (e.g. UseRedisBackplane()) for multi-node deployments.");
        }

        var handler = _requestHandler;
        if (handler is null)
        {
            return new BackplaneResponse(
                string.Empty,
                false,
                ReadOnlyMemory<byte>.Empty,
                "No request handler registered on this node.");
        }

        var request = new BackplaneRequest(Guid.NewGuid().ToString("N"), NodeId, type, payload);
        return await handler(request);
    }

    public Task<IAsyncDisposable> RegisterRequestHandlerAsync(Func<BackplaneRequest, Task<BackplaneResponse>> handler, CancellationToken ct = default)
    {
        _requestHandler = handler;
        return Task.FromResult<IAsyncDisposable>(new HandlerRegistration(this, handler));
    }

    private void Unsubscribe(string documentId, Func<BackplaneMessage, ValueTask> handler)
    {
        _subscribers.AddOrUpdate(
            documentId,
            _ => ImmutableList<Func<BackplaneMessage, ValueTask>>.Empty,
            (_, list) => list.Remove(handler));
    }

    private sealed class Subscription(LocalBackplane parent, string documentId, Func<BackplaneMessage, ValueTask> handler) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            parent.Unsubscribe(documentId, handler);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class HandlerRegistration(LocalBackplane parent, Func<BackplaneRequest, Task<BackplaneResponse>> handler) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Interlocked.CompareExchange(ref parent._requestHandler, null, handler);
            return ValueTask.CompletedTask;
        }
    }
}
