using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using OpStream.Shared.Messages;

namespace OpStream.Client.Transports.WebSockets;

/// <summary>
/// A WebSocket-based implementation of the OpStream client transport.
/// Handles bidirectional communication using JSON messages over a single WebSocket.
/// </summary>
public class WebSocketOpStreamClient : IOpStreamClient
{
    private readonly ClientWebSocket _webSocket = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<WebSocketMessage>> _pendingRequests = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _listenTask;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
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
    /// Initializes a new instance of the WebSocketOpStreamClient.
    /// </summary>
    /// <param name="options">Configuration options containing the server URI.</param>
    public WebSocketOpStreamClient(IOptions<OpStreamWebSocketOptions> options)
    {
        _listenTask = ListenLoop(new Uri(options.Value.ServerUri));
    }

    private async Task ListenLoop(Uri uri)
    {
        try
        {
            await _webSocket.ConnectAsync(uri, _cts.Token);

            while (_webSocket.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
            {
                var json = await ReceiveTextAsync(_webSocket, _cts.Token);
                if (json == null) break;

                var message = JsonSerializer.Deserialize<WebSocketMessage>(json, JsonOptions);
                if (message == null) continue;

                if (!string.IsNullOrEmpty(message.CorrelationId) && _pendingRequests.TryRemove(message.CorrelationId, out var tcs))
                {
                    tcs.SetResult(message);
                    continue;
                }

                // Handle events
                switch (message.MessageType)
                {
                    case WebSocketOpMessageType.ReceiveOpEvent:
                        if (OnReceiveOp != null && message.ReceiveOpEvent != null)
                        {
                            await OnReceiveOp.Invoke(message.ReceiveOpEvent.Payload, message.ReceiveOpEvent.NewRevision);
                        }
                        break;
                    case WebSocketOpMessageType.ReceiveAwarenessEvent:
                        if (OnReceiveAwareness != null && message.ReceiveAwarenessEvent != null)
                        {
                            await OnReceiveAwareness.Invoke(message.ReceiveAwarenessEvent.Awareness);
                        }
                        break;
                    case WebSocketOpMessageType.PeerDisconnectedEvent:
                        if (message.PeerDisconnectedEvent != null)
                        {
                            OnPeerDisconnected?.Invoke(message.PeerDisconnectedEvent.PeerId);
                        }
                        break;
                    case WebSocketOpMessageType.ReceiveCommentCreated:
                        if (_onCommentCreated != null && message.ReceiveCommentCreated is JsonElement createdEl)
                        {
                            var dto = createdEl.Deserialize<CommentDto>(JsonOptions);
                            if (dto != null) await _onCommentCreated.Invoke(dto);
                        }
                        break;
                    case WebSocketOpMessageType.ReceiveCommentUpdated:
                        if (_onCommentUpdated != null && message.ReceiveCommentUpdated is JsonElement updatedEl)
                        {
                            var dto = updatedEl.Deserialize<CommentDto>(JsonOptions);
                            if (dto != null) await _onCommentUpdated.Invoke(dto);
                        }
                        break;
                    case WebSocketOpMessageType.ReceiveCommentDeleted:
                        if (_onCommentDeleted != null && message.ReceiveCommentDeleted != null)
                        {
                            await _onCommentDeleted.Invoke(new CommentDeletedDto(message.ReceiveCommentDeleted.CommentId));
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OnDisconnected?.Invoke(ex);
        }
        finally
        {
            if (_webSocket.State == WebSocketState.Open || _webSocket.State == WebSocketState.CloseReceived)
            {
                try { await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None); } catch { }
            }
        }
    }

    private async Task<string?> ReceiveTextAsync(WebSocket webSocket, CancellationToken ct)
    {
        var buffer = new byte[1024 * 4];
        using var ms = new MemoryStream();
        
        WebSocketReceiveResult result;
        do
        {
            result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        ms.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(ms, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    /// <inheritdoc/>
    public async Task<ClientJoinResult> ConnectAndJoinAsync(string documentId, string documentType, CancellationToken ct = default)
    {
        // Wait for connection to be established in ListenLoop
        while (_webSocket.State == WebSocketState.None || _webSocket.State == WebSocketState.Connecting)
        {
            await Task.Delay(10, ct);
        }

        if (_webSocket.State != WebSocketState.Open)
        {
            throw new Exception("WebSocket is not open");
        }

        var correlationId = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<WebSocketMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[correlationId] = tcs;

        var request = new WebSocketMessage
        {
            CorrelationId = correlationId,
            MessageType = WebSocketOpMessageType.JoinRequest,
            JoinRequest = new JoinRequestData(documentId, documentType, 1)
        };

        await SendMessageAsync(request, ct);

        using var registration = ct.Register(() => tcs.TrySetCanceled());
        var response = await tcs.Task;

        if (response.MessageType == WebSocketOpMessageType.ErrorResponse)
        {
            throw new Exception(response.ErrorMessage);
        }

        if (response.MessageType != WebSocketOpMessageType.JoinResponse || response.JoinResponse == null)
        {
            throw new Exception("Unexpected response during Join");
        }

        var jr = response.JoinResponse;
        return new ClientJoinResult(jr.Revision, jr.Snapshot, jr.Awareness);
    }

    /// <inheritdoc/>
    public async Task<ClientOpResult> SendOpAsync(string documentId, ReadOnlyMemory<byte> payload, long baseRevision, CancellationToken ct = default)
    {
        var correlationId = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<WebSocketMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[correlationId] = tcs;

        var request = new WebSocketMessage
        {
            CorrelationId = correlationId,
            MessageType = WebSocketOpMessageType.OpRequest,
            OpRequest = new OpRequestData(documentId, payload.ToArray(), baseRevision)
        };

        await SendMessageAsync(request, ct);

        using var registration = ct.Register(() => tcs.TrySetCanceled());
        var response = await tcs.Task;

        if (response.MessageType == WebSocketOpMessageType.ErrorResponse)
        {
            throw new Exception(response.ErrorMessage);
        }

        if (response.MessageType != WebSocketOpMessageType.OpResponse || response.OpResponse == null)
        {
            throw new Exception("Unexpected response during SendOp");
        }

        var or = response.OpResponse;
        return new ClientOpResult(or.Success, or.NewRevision, or.ErrorMessage);
    }

    /// <inheritdoc/>
    public async Task SendAwarenessAsync(string documentId, JsonElement data, CancellationToken ct = default)
    {
        var request = new WebSocketMessage
        {
            MessageType = WebSocketOpMessageType.AwarenessRequest,
            AwarenessRequest = new AwarenessRequestData(documentId, data.GetRawText())
        };

        await SendMessageAsync(request, ct);
    }

    // ─── Comments ────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<List<CommentDto>> ListOpenCommentsAsync(string documentId, CancellationToken ct = default)
    {
        var response = await SendCommentRequestAsync(
            WebSocketOpMessageType.ListOpenComments,
            new CommentCommandData { DocumentId = documentId },
            ct);

        if (response.CommentResponse is not JsonElement el)
            throw new Exception("Unexpected response for ListOpenComments.");
        return el.Deserialize<List<CommentDto>>(JsonOptions) ?? new List<CommentDto>();
    }

    /// <inheritdoc/>
    public async Task<CommentDto> CreateCommentAsync(string documentId, NewCommentCmd cmd, CancellationToken ct = default)
    {
        JsonElement? anchorEl = cmd.Anchor is null
            ? null
            : JsonSerializer.SerializeToElement(cmd.Anchor, JsonOptions);

        var response = await SendCommentRequestAsync(
            WebSocketOpMessageType.CreateComment,
            new CommentCommandData
            {
                DocumentId = documentId,
                Body = cmd.Body,
                ParentCommentId = cmd.ParentCommentId,
                Anchor = anchorEl
            }, ct);

        return DeserializeCommentResponse(response);
    }

    /// <inheritdoc/>
    public async Task<CommentDto> EditCommentAsync(string documentId, string commentId, string newBody, CancellationToken ct = default)
    {
        var response = await SendCommentRequestAsync(
            WebSocketOpMessageType.EditComment,
            new CommentCommandData { DocumentId = documentId, CommentId = commentId, Body = newBody },
            ct);

        return DeserializeCommentResponse(response);
    }

    /// <inheritdoc/>
    public async Task<CommentDto> ResolveCommentAsync(string documentId, string commentId, CancellationToken ct = default)
    {
        var response = await SendCommentRequestAsync(
            WebSocketOpMessageType.ResolveComment,
            new CommentCommandData { DocumentId = documentId, CommentId = commentId },
            ct);

        return DeserializeCommentResponse(response);
    }

    /// <inheritdoc/>
    public async Task DeleteCommentAsync(string documentId, string commentId, CancellationToken ct = default)
    {
        await SendCommentRequestAsync(
            WebSocketOpMessageType.DeleteComment,
            new CommentCommandData { DocumentId = documentId, CommentId = commentId },
            ct);
    }

    private async Task<WebSocketMessage> SendCommentRequestAsync(
        WebSocketOpMessageType messageType, CommentCommandData cmd, CancellationToken ct)
    {
        var correlationId = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<WebSocketMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[correlationId] = tcs;

        await SendMessageAsync(new WebSocketMessage
        {
            CorrelationId = correlationId,
            MessageType = messageType,
            CommentCommand = cmd
        }, ct);

        using var registration = ct.Register(() => tcs.TrySetCanceled());
        var response = await tcs.Task;

        if (response.MessageType == WebSocketOpMessageType.ErrorResponse)
            throw new Exception(response.ErrorMessage ?? "Comment operation failed.");

        return response;
    }

    private static CommentDto DeserializeCommentResponse(WebSocketMessage response)
    {
        if (response.CommentResponse is not JsonElement el)
            throw new Exception("Unexpected response format for comment operation.");
        return el.Deserialize<CommentDto>(JsonOptions)
            ?? throw new Exception("Null comment in server response.");
    }

    private async Task SendMessageAsync(WebSocketMessage message, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(message, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_webSocket.State == WebSocketState.Open)
        {
            try { await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", CancellationToken.None); } catch { }
        }
        _webSocket.Dispose();
        try { await _listenTask; } catch { }
        _cts.Dispose();
    }
}
