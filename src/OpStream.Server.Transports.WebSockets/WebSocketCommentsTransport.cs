using Microsoft.AspNetCore.Http;
using OpStream.Server.Comments;
using OpStream.Shared.Messages;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace OpStream.Server.Transports.WebSockets;

/// <summary>
/// WebSocket comments endpoint. Accepts JSON-encoded <see cref="WebSocketMessage"/> envelopes
/// for the five comment hub methods (CreateComment, EditComment, ResolveComment,
/// DeleteComment, ListOpenComments) and responds in kind.
/// <para>
/// Server-push events (CommentCreated / CommentUpdated / CommentDeleted) are delivered via
/// <see cref="WebSocketBackplaneRelay"/>, not via this per-connection handler.
/// </para>
/// </summary>
public class WebSocketCommentsTransport(CommentRouter commentRouter)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();

        // Derive peerId from a header or query string; fall back to a random id.
        var peerId = context.Request.Query["peerId"].FirstOrDefault()
                  ?? context.Request.Headers["X-Peer-Id"].FirstOrDefault()
                  ?? Guid.NewGuid().ToString("N");

        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                var json = await ReceiveTextAsync(webSocket, context.RequestAborted);
                if (json is null) break;

                WebSocketMessage? request;
                try { request = JsonSerializer.Deserialize<WebSocketMessage>(json, JsonOptions); }
                catch { request = null; }

                if (request is not null)
                    await HandleRequest(webSocket, peerId, request, context.RequestAborted);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"WebSocket comments error: {ex.Message}");
        }
        finally
        {
            if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try { await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None); }
                catch { }
            }
        }
    }

    private async Task HandleRequest(WebSocket ws, string peerId, WebSocketMessage request, CancellationToken ct)
    {
        WebSocketMessage response;
        try
        {
            response = await DispatchAsync(peerId, request, ct);
        }
        catch (Exception ex)
        {
            response = Error(request.CorrelationId, ex.Message);
        }

        response.CorrelationId ??= request.CorrelationId;
        var json = JsonSerializer.Serialize(response, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    private async Task<WebSocketMessage> DispatchAsync(string peerId, WebSocketMessage request, CancellationToken ct)
    {
        var cmd = request.CommentCommand;
        switch (request.MessageType)
        {
            case WebSocketOpMessageType.ListOpenComments:
            {
                RequireDocumentId(cmd);
                var result = await commentRouter.ListOpenAsync(cmd!.DocumentId!, ct);
                if (!result.Success) return Error(request.CorrelationId, result.ErrorMessage);
                return new WebSocketMessage
                {
                    CorrelationId = request.CorrelationId,
                    MessageType = WebSocketOpMessageType.ListOpenComments,
                    CommentResponse = result.Value
                };
            }

            case WebSocketOpMessageType.CreateComment:
            {
                RequireDocumentId(cmd);
                if (string.IsNullOrWhiteSpace(cmd!.Body))
                    throw new InvalidOperationException("'body' is required for CreateComment.");

                Anchor? anchor = null;
                if (cmd.Anchor.HasValue && cmd.Anchor.Value.ValueKind != JsonValueKind.Null)
                    anchor = JsonSerializer.Deserialize<Anchor>(cmd.Anchor.Value.GetRawText(), JsonOptions);

                var newCmd = new NewCommentCmd(cmd.Body, anchor, cmd.ParentCommentId);
                var result = await commentRouter.CreateAsync(peerId, cmd.DocumentId!, newCmd, ct);
                if (!result.Success) return Error(request.CorrelationId, result.ErrorMessage);
                return new WebSocketMessage
                {
                    CorrelationId = request.CorrelationId,
                    MessageType = WebSocketOpMessageType.CreateComment,
                    CommentResponse = result.Value
                };
            }

            case WebSocketOpMessageType.EditComment:
            {
                RequireDocumentId(cmd);
                RequireCommentId(cmd);
                if (string.IsNullOrWhiteSpace(cmd!.Body))
                    throw new InvalidOperationException("'body' is required for EditComment.");
                var result = await commentRouter.EditAsync(peerId, cmd.DocumentId!, cmd.CommentId!, cmd.Body, ct);
                if (!result.Success) return Error(request.CorrelationId, result.ErrorMessage);
                return new WebSocketMessage
                {
                    CorrelationId = request.CorrelationId,
                    MessageType = WebSocketOpMessageType.EditComment,
                    CommentResponse = result.Value
                };
            }

            case WebSocketOpMessageType.ResolveComment:
            {
                RequireDocumentId(cmd);
                RequireCommentId(cmd);
                var result = await commentRouter.ResolveAsync(peerId, cmd!.DocumentId!, cmd.CommentId!, ct);
                if (!result.Success) return Error(request.CorrelationId, result.ErrorMessage);
                return new WebSocketMessage
                {
                    CorrelationId = request.CorrelationId,
                    MessageType = WebSocketOpMessageType.ResolveComment,
                    CommentResponse = result.Value
                };
            }

            case WebSocketOpMessageType.DeleteComment:
            {
                RequireDocumentId(cmd);
                RequireCommentId(cmd);
                var result = await commentRouter.DeleteAsync(peerId, cmd!.DocumentId!, cmd.CommentId!, ct);
                if (!result.Success) return Error(request.CorrelationId, result.ErrorMessage);
                return new WebSocketMessage
                {
                    CorrelationId = request.CorrelationId,
                    MessageType = WebSocketOpMessageType.DeleteComment
                };
            }

            default:
                return Error(request.CorrelationId, $"Unknown comment command: '{request.MessageType}'.");
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static WebSocketMessage Error(string? correlationId, string? message) => new()
    {
        CorrelationId = correlationId,
        MessageType = WebSocketOpMessageType.ErrorResponse,
        ErrorMessage = message ?? "Unknown error"
    };

    private static void RequireDocumentId(CommentCommandData? cmd)
    {
        if (cmd is null || string.IsNullOrWhiteSpace(cmd.DocumentId))
            throw new InvalidOperationException("'documentId' is required.");
    }

    private static void RequireCommentId(CommentCommandData? cmd)
    {
        if (cmd is null || string.IsNullOrWhiteSpace(cmd.CommentId))
            throw new InvalidOperationException("'commentId' is required.");
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[1024 * 4];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        ms.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(ms, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
    }
}
