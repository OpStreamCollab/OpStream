namespace OpStream.Shared.Abstractions;

/// <summary>
/// Contract for scaling out to multiple nodes (e.g., Redis, NATS).
/// </summary>
public interface IBackplane
{
    string NodeId { get; }

    /// <summary>
    /// Subscribes a handler to events for a specific document across the cluster.
    /// </summary>
    Task<IAsyncDisposable> SubscribeAsync(string documentId, Func<BackplaneMessage, ValueTask> handler, CancellationToken ct = default);
    
    /// <summary>
    /// Broadcasts an event to all nodes subscribed to the document.
    /// </summary>
    Task PublishAsync(string documentId, BackplaneMessage message, CancellationToken ct = default);

    /// <summary>
    /// Sends a request to a specific node and waits for a response.
    /// </summary>
    Task<BackplaneResponse> SendRequestAsync(string targetNodeId, string type, ReadOnlyMemory<byte> payload, CancellationToken ct = default);

    /// <summary>
    /// Registers a handler for incoming requests to this node.
    /// </summary>
    Task<IAsyncDisposable> RegisterRequestHandlerAsync(Func<BackplaneRequest, Task<BackplaneResponse>> handler, CancellationToken ct = default);
}

/// <summary>
/// A message transmitted across the backplane. Payload is raw UTF-8 bytes.
/// </summary>
public record BackplaneMessage(
    string SenderNodeId, 
    string Type, 
    ReadOnlyMemory<byte> Payload,
    string? SenderPeerId = null);

/// <summary>
/// A request sent to a specific node.
/// </summary>
public record BackplaneRequest(
    string RequestId,
    string SenderNodeId,
    string Type,
    ReadOnlyMemory<byte> Payload);

/// <summary>
/// A response to a BackplaneRequest.
/// </summary>
public record BackplaneResponse(
    string RequestId,
    bool Success,
    ReadOnlyMemory<byte> Payload,
    string? ErrorMessage = null);
