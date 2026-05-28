using Grpc.Net.Client;
using Grpc.Core;
using OpStream.Shared.Messages.gRPC;
using OpStream.Shared.Messages;
using System.Text.Json;
using System.Collections.Concurrent;
using OpStream.Constants;
using Microsoft.Extensions.Options;

namespace OpStream.Client.Transports.gRPC;

/// <summary>
/// A gRPC-based implementation of the OpStream client transport.
/// Handles bidirectional streaming and request-response patterns over a single stream.
/// </summary>
public class gRPCOpStreamClient : IOpStreamClient
{
    private readonly GrpcChannel _channel;
    private readonly OpStreamService.OpStreamServiceClient _client;
    private readonly AsyncDuplexStreamingCall<ClientMessage, ServerMessage> _call;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ServerMessage>> _pendingRequests = new();
    private readonly Task _listenTask;
    private readonly CancellationTokenSource _cts = new();

    /// <inheritdoc/>
    public event Func<ReadOnlyMemory<byte>, long, Task>? OnReceiveOp;
    
    /// <inheritdoc/>
    public event Action<Exception?>? OnDisconnected;
    
    /// <inheritdoc/>
    public event Func<IEnumerable<AwarenessState>, Task>? OnReceiveAwareness;

    /// <inheritdoc/>
    public event Action<string>? OnPeerDisconnected;

    /// <summary>
    /// Initializes a new instance of the gRPCOpStreamClient.
    /// </summary>
    /// <param name="options">Configuration options containing the server address.</param>
    public gRPCOpStreamClient(IOptions<OpStreamgRPCOptions> options)
    {
        _channel = GrpcChannel.ForAddress(options.Value.ServerAddress);
        _client = new OpStreamService.OpStreamServiceClient(_channel);
        _call = _client.Connect();
        _listenTask = ListenLoop();
    }

    /// <summary>
    /// Background task that listens for incoming messages from the server.
    /// </summary>
    private async Task ListenLoop()
    {
        try
        {
            await foreach (var message in _call.ResponseStream.ReadAllAsync(_cts.Token))
            {
                if (!string.IsNullOrEmpty(message.CorrelationId) && _pendingRequests.TryRemove(message.CorrelationId, out var tcs))
                {
                    tcs.SetResult(message);
                    continue;
                }

                // Handle events
                switch (message.MessageTypeCase)
                {
                    case ServerMessage.MessageTypeOneofCase.ReceiveOpEvent:
                        if (OnReceiveOp != null)
                        {
                            await OnReceiveOp.Invoke(message.ReceiveOpEvent.Payload.ToByteArray(), message.ReceiveOpEvent.NewRevision);
                        }
                        break;
                    case ServerMessage.MessageTypeOneofCase.ReceiveAwarenessEvent:
                        if (OnReceiveAwareness != null)
                        {
                            await OnReceiveAwareness.Invoke(message.ReceiveAwarenessEvent.Awareness.Select(FromProto));
                        }
                        break;
                    case ServerMessage.MessageTypeOneofCase.PeerDisconnectedEvent:
                        OnPeerDisconnected?.Invoke(message.PeerDisconnectedEvent.PeerId);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OnDisconnected?.Invoke(ex);
        }
    }

    /// <inheritdoc/>
    public async Task<ClientJoinResult> ConnectAndJoinAsync(string documentId, string documentType, CancellationToken ct = default)
    {
        var correlationId = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<ServerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[correlationId] = tcs;

        await _call.RequestStream.WriteAsync(new ClientMessage
        {
            CorrelationId = correlationId,
            JoinRequest = new JoinRequest
            {
                DocumentId = documentId,
                DocumentType = documentType,
                ClientProtoVersion = 1
            }
        });

        using var registration = ct.Register(() => tcs.TrySetCanceled());
        var response = await tcs.Task;
        
        if (response.MessageTypeCase != ServerMessage.MessageTypeOneofCase.JoinResponse)
        {
            throw new Exception("Unexpected response during Join");
        }

        var jr = response.JoinResponse;
        return new ClientJoinResult(jr.Revision, jr.Snapshot.ToByteArray(), jr.Awareness.Select(FromProto));
    }

    /// <inheritdoc/>
    public async Task<ClientOpResult> SendOpAsync(string documentId, ReadOnlyMemory<byte> payload, long baseRevision, CancellationToken ct = default)
    {
        var correlationId = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<ServerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[correlationId] = tcs;

        await _call.RequestStream.WriteAsync(new ClientMessage
        {
            CorrelationId = correlationId,
            OpRequest = new OpRequest
            {
                DocumentId = documentId,
                Payload = Google.Protobuf.ByteString.CopyFrom(payload.Span),
                BaseRevision = baseRevision
            }
        });

        using var registration = ct.Register(() => tcs.TrySetCanceled());
        var response = await tcs.Task;

        if (response.MessageTypeCase != ServerMessage.MessageTypeOneofCase.OpResponse)
        {
            throw new Exception("Unexpected response during SendOp");
        }

        var or = response.OpResponse;
        return new ClientOpResult(or.Success, or.NewRevision, or.ErrorMessage);
    }

    /// <inheritdoc/>
    public async Task SendAwarenessAsync(string documentId, JsonElement data, CancellationToken ct = default)
    {
        await _call.RequestStream.WriteAsync(new ClientMessage
        {
            CorrelationId = "", // No correlation needed for one-way messages
            AwarenessRequest = new AwarenessRequest
            {
                DocumentId = documentId,
                DataJson = data.GetRawText()
            }
        });
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await _call.RequestStream.CompleteAsync();
        }
        catch { }
        _call.Dispose();
        _channel.Dispose();
        try
        {
            await _listenTask;
        }
        catch { }
        _cts.Dispose();
    }

    private static AwarenessState FromProto(AwarenessStateProto proto)
    {
        return new AwarenessState(
            proto.PeerId,
            JsonDocument.Parse(proto.DataJson).RootElement.Clone(),
            proto.LastUpdated.ToDateTimeOffset()
        );
    }
}
