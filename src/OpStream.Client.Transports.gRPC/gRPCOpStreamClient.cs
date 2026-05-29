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
    private readonly string _peerId = Guid.NewGuid().ToString();
    private readonly GrpcChannel _channel;
    private readonly OpStreamService.OpStreamServiceClient _client;
    private readonly OpStreamCommentsService.OpStreamCommentsServiceClient _commentsClient;
    private readonly AsyncDuplexStreamingCall<ClientMessage, ServerMessage> _call;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ServerMessage>> _pendingRequests = new();
    private readonly Task _listenTask;
    private readonly CancellationTokenSource _cts = new();
    private Task? _commentListenTask;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <inheritdoc/>
    public event Func<ReadOnlyMemory<byte>, long, Task>? OnReceiveOp;

    /// <inheritdoc/>
    public event Action<Exception?>? OnDisconnected;

    /// <inheritdoc/>
    public event Func<IEnumerable<AwarenessState>, Task>? OnReceiveAwareness;

    /// <inheritdoc/>
    public event Action<string>? OnPeerDisconnected;

    // ─── Comment events ───────────────────────────────────────────────────────

    private event Func<CommentDto, Task>? _onCommentCreated;
    private event Func<CommentDto, Task>? _onCommentUpdated;
    private event Func<CommentDeletedDto, Task>? _onCommentDeleted;

    /// <inheritdoc/>
    public event Func<CommentDto, Task>? OnCommentCreated
    {
        add    => _onCommentCreated += value;
        remove => _onCommentCreated -= value;
    }

    /// <inheritdoc/>
    public event Func<CommentDto, Task>? OnCommentUpdated
    {
        add    => _onCommentUpdated += value;
        remove => _onCommentUpdated -= value;
    }

    /// <inheritdoc/>
    public event Func<CommentDeletedDto, Task>? OnCommentDeleted
    {
        add    => _onCommentDeleted += value;
        remove => _onCommentDeleted -= value;
    }

    /// <summary>
    /// Initializes a new instance of the gRPCOpStreamClient.
    /// </summary>
    /// <param name="options">Configuration options containing the server address.</param>
    public gRPCOpStreamClient(IOptions<OpStreamgRPCOptions> options)
    {
        _channel = GrpcChannel.ForAddress(options.Value.ServerAddress);
        _client = new OpStreamService.OpStreamServiceClient(_channel);
        _commentsClient = new OpStreamCommentsService.OpStreamCommentsServiceClient(_channel);
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
        _commentListenTask = ListenCommentsLoop(documentId, _cts.Token);
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

    // ─── Comment subscription ─────────────────────────────────────────────────

    private async Task ListenCommentsLoop(string documentId, CancellationToken ct)
    {
        try
        {
            using var call = _commentsClient.SubscribeComments(
                new SubscribeCommentsRequest { DocumentId = documentId },
                cancellationToken: ct);

            await foreach (var evt in call.ResponseStream.ReadAllAsync(ct))
            {
                switch (evt.EventTypeCase)
                {
                    case CommentEvent.EventTypeOneofCase.Created:
                        if (_onCommentCreated != null)
                            await _onCommentCreated.Invoke(ProtoToDto(evt.Created));
                        break;
                    case CommentEvent.EventTypeOneofCase.Updated:
                        if (_onCommentUpdated != null)
                            await _onCommentUpdated.Invoke(ProtoToDto(evt.Updated));
                        break;
                    case CommentEvent.EventTypeOneofCase.DeletedCommentId:
                        if (_onCommentDeleted != null)
                            await _onCommentDeleted.Invoke(new CommentDeletedDto(evt.DeletedCommentId));
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) { }
    }

    // ─── Comment methods ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<List<CommentDto>> ListOpenCommentsAsync(string documentId, CancellationToken ct = default)
    {
        var response = await _commentsClient.ListOpenCommentsAsync(
            new ListCommentsRequest { DocumentId = documentId }, cancellationToken: ct);
        return response.Comments.Select(ProtoToDto).ToList();
    }

    /// <inheritdoc/>
    public async Task<CommentDto> CreateCommentAsync(string documentId, NewCommentCmd cmd, CancellationToken ct = default)
    {
        var request = new CreateCommentRequest
        {
            PeerId = _peerId,
            DocumentId = documentId,
            Body = cmd.Body,
            AnchorJson = cmd.Anchor is null
                ? string.Empty
                : JsonSerializer.Serialize(cmd.Anchor, JsonOptions),
            ParentCommentId = cmd.ParentCommentId ?? string.Empty
        };
        var response = await _commentsClient.CreateCommentAsync(request, cancellationToken: ct);
        if (!response.Success) throw new InvalidOperationException(response.ErrorMessage);
        return ProtoToDto(response.Comment);
    }

    /// <inheritdoc/>
    public async Task<CommentDto> EditCommentAsync(string documentId, string commentId, string newBody, CancellationToken ct = default)
    {
        var request = new EditCommentRequest
        {
            PeerId = _peerId,
            DocumentId = documentId,
            CommentId = commentId,
            NewBody = newBody
        };
        var response = await _commentsClient.EditCommentAsync(request, cancellationToken: ct);
        if (!response.Success) throw new InvalidOperationException(response.ErrorMessage);
        return ProtoToDto(response.Comment);
    }

    /// <inheritdoc/>
    public async Task<CommentDto> ResolveCommentAsync(string documentId, string commentId, CancellationToken ct = default)
    {
        var request = new CommentActionRequest
        {
            PeerId = _peerId,
            DocumentId = documentId,
            CommentId = commentId
        };
        var response = await _commentsClient.ResolveCommentAsync(request, cancellationToken: ct);
        if (!response.Success) throw new InvalidOperationException(response.ErrorMessage);
        return ProtoToDto(response.Comment);
    }

    /// <inheritdoc/>
    public async Task DeleteCommentAsync(string documentId, string commentId, CancellationToken ct = default)
    {
        var request = new CommentActionRequest
        {
            PeerId = _peerId,
            DocumentId = documentId,
            CommentId = commentId
        };
        var response = await _commentsClient.DeleteCommentAsync(request, cancellationToken: ct);
        if (!response.Success) throw new InvalidOperationException(response.ErrorMessage);
    }

    // ─── Mapping ──────────────────────────────────────────────────────────────

    private static CommentDto ProtoToDto(CommentProto p) => new(
        p.Id,
        p.DocumentId,
        string.IsNullOrEmpty(p.ParentCommentId) ? null : p.ParentCommentId,
        p.AuthorPeerId,
        p.Body,
        string.IsNullOrEmpty(p.AnchorJson)
            ? null
            : JsonSerializer.Deserialize<AnchorDto>(p.AnchorJson, JsonOptions),
        p.AnchoredAtRevision,
        p.CreatedAt.ToDateTimeOffset(),
        p.ResolvedAt?.ToDateTimeOffset(),
        string.IsNullOrEmpty(p.ResolvedByPeerId) ? null : p.ResolvedByPeerId,
        p.IsOrphaned
    );

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
        try { await _listenTask; } catch { }
        if (_commentListenTask != null)
            try { await _commentListenTask; } catch { }
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
