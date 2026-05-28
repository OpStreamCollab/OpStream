using System.Collections.Concurrent;
using StackExchange.Redis;
using OpStream.Server.Session;
using OpStream.Shared.Abstractions;

namespace OpStream.Server.Backplane.Redis;

/// <summary>
/// Implements a Redis-based backplane for multi-node communication in OpStream.
/// </summary>
public class RedisBackplane : IBackplane, IAsyncDisposable
{
    private readonly ConnectionMultiplexer _redis;
    private readonly ISubscriber _subscriber;
    private readonly string _nodeId;
    private readonly string _responseChannel;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BackplaneResponse>> _pendingRequests = new();
    private Func<BackplaneRequest, Task<BackplaneResponse>>? _requestHandler;

    /// <summary>
    /// Gets the unique identifier for this node.
    /// </summary>
    public string NodeId => _nodeId;

    /// <summary>Underlying multiplexer, exposed for health checks and diagnostics.</summary>
    internal IConnectionMultiplexer Multiplexer => _redis;

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisBackplane"/> class.
    /// </summary>
    /// <param name="connectionString">The Redis connection string.</param>
    /// <param name="nodeId">An optional unique identifier for this node. If not provided, a new GUID will be generated.</param>
    public RedisBackplane(string connectionString, string? nodeId = null)
    {
        _redis = ConnectionMultiplexer.Connect(connectionString);
        _subscriber = _redis.GetSubscriber();
        _nodeId = nodeId ?? Guid.NewGuid().ToString("N");
        _responseChannel = $"OpStream:Responses:{_nodeId}";

        // Listen for responses to our requests
        _subscriber.Subscribe(RedisChannel.Literal(_responseChannel), (channel, message) =>
        {
            var response = DeserializeResponse(message);
            if (_pendingRequests.TryRemove(response.RequestId, out var tcs))
            {
                tcs.SetResult(response);
            }
        });

        // Listen for requests to this node
        _subscriber.Subscribe(RedisChannel.Literal($"OpStream:Requests:{_nodeId}"), async (channel, message) =>
        {
            if (_requestHandler != null)
            {
                var request = DeserializeRequest(message);
                var response = await _requestHandler(request);
                await _subscriber.PublishAsync(RedisChannel.Literal($"OpStream:Responses:{request.SenderNodeId}"), SerializeResponse(response));
            }
        });
    }

    /// <summary>
    /// Subscribes to messages for a specific document.
    /// </summary>
    /// <param name="documentId">The ID of the document to subscribe to.</param>
    /// <param name="handler">The handler for incoming backplane messages.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>An <see cref="IAsyncDisposable"/> that can be used to unsubscribe.</returns>
    public async Task<IAsyncDisposable> SubscribeAsync(string documentId, Func<BackplaneMessage, ValueTask> handler, CancellationToken ct = default)
    {
        var channel = RedisChannel.Literal($"OpStream:Docs:{documentId}");
        await _subscriber.SubscribeAsync(channel, async (c, m) =>
        {
            await handler(DeserializeMessage(m));
        });

        return new SubscriptionDisposable(_subscriber, channel);
    }

    /// <summary>
    /// Publishes a message for a specific document to the backplane.
    /// </summary>
    /// <param name="documentId">The ID of the document.</param>
    /// <param name="message">The message to publish.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task PublishAsync(string documentId, BackplaneMessage message, CancellationToken ct = default)
    {
        var channel = RedisChannel.Literal($"OpStream:Docs:{documentId}");
        await _subscriber.PublishAsync(channel, SerializeMessage(message));
    }

    /// <summary>
    /// Sends a request to a specific target node and waits for a response.
    /// </summary>
    /// <param name="targetNodeId">The ID of the target node.</param>
    /// <param name="type">The type of the request.</param>
    /// <param name="payload">The request payload.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The backplane response.</returns>
    public async Task<BackplaneResponse> SendRequestAsync(string targetNodeId, string type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var request = new BackplaneRequest(requestId, _nodeId, type, payload);
        var tcs = new TaskCompletionSource<BackplaneResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        
        _pendingRequests[requestId] = tcs;

        await _subscriber.PublishAsync(RedisChannel.Literal($"OpStream:Requests:{targetNodeId}"), SerializeRequest(request));

        using var registration = ct.Register(() => tcs.TrySetCanceled());

        try
        {
            return await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            _pendingRequests.TryRemove(requestId, out _);
            return new BackplaneResponse(requestId, false, ReadOnlyMemory<byte>.Empty, "Request timed out or cancelled.");
        }
    }

    /// <summary>
    /// Registers a handler for incoming requests to this node.
    /// </summary>
    /// <param name="handler">The request handler.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>An <see cref="IAsyncDisposable"/> representing the registration.</returns>
    public Task<IAsyncDisposable> RegisterRequestHandlerAsync(Func<BackplaneRequest, Task<BackplaneResponse>> handler, CancellationToken ct = default)
    {
        _requestHandler = handler;
        return Task.FromResult<IAsyncDisposable>(new NoopDisposable());
    }

    /// <summary>
    /// Disposes the Redis connection.
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous operation.</returns>
    public async ValueTask DisposeAsync()
    {
        await _redis.DisposeAsync();
    }

    private byte[] SerializeMessage(BackplaneMessage message) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(message);
    private BackplaneMessage DeserializeMessage(RedisValue value) => System.Text.Json.JsonSerializer.Deserialize<BackplaneMessage>((byte[])value!)!;

    private byte[] SerializeRequest(BackplaneRequest request) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request);
    private BackplaneRequest DeserializeRequest(RedisValue value) => System.Text.Json.JsonSerializer.Deserialize<BackplaneRequest>((byte[])value!)!;

    private byte[] SerializeResponse(BackplaneResponse response) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(response);
    private BackplaneResponse DeserializeResponse(RedisValue value) => System.Text.Json.JsonSerializer.Deserialize<BackplaneResponse>((byte[])value!)!;

    private class SubscriptionDisposable(ISubscriber subscriber, RedisChannel channel) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await subscriber.UnsubscribeAsync(channel);
        }
    }

    private class NoopDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
