using Microsoft.AspNetCore.Http;
using OpStream.Constants;
using OpStream.Server.Models;
using OpStream.Server.Session;
using OpStream.Shared.Messages;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace OpStream.Server.Transports.WebSockets;

/// <summary>
/// WebSocket management endpoint. Accepts JSON-encoded <see cref="WebSocketMgmtRequest"/> messages
/// and responds with <see cref="WebSocketMgmtResponse"/> messages.
/// <para>
/// All commands are routed through <see cref="DatabaseCommandRouter"/>, which applies tenant
/// scoping, authorization, and owner-node routing — identical to the SignalR management hub.
/// </para>
/// </summary>
public class WebSocketManagementTransport(DatabaseCommandRouter router)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();

        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                var json = await ReceiveTextAsync(webSocket, context.RequestAborted);
                if (json == null) break;

                var request = JsonSerializer.Deserialize<WebSocketMgmtRequest>(json, JsonOptions);
                if (request != null)
                    await HandleRequest(webSocket, request, context.RequestAborted);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"WebSocket management error: {ex.Message}");
        }
        finally
        {
            if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
            {
                try { await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None); }
                catch { }
            }
        }
    }

    private async Task HandleRequest(WebSocket webSocket, WebSocketMgmtRequest request, CancellationToken ct)
    {
        WebSocketMgmtResponse response;
        try
        {
            response = await DispatchAsync(request, ct);
        }
        catch (Exception ex)
        {
            response = Error(request.CorrelationId, ex.Message);
        }

        response.CorrelationId ??= request.CorrelationId;
        var json = JsonSerializer.Serialize(response, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    private async Task<WebSocketMgmtResponse> DispatchAsync(WebSocketMgmtRequest request, CancellationToken ct)
    {
        switch (request.Command)
        {
            case OpStreamConstants.ManagementHubMethods.ListDocuments:
            {
                var result = await router.ListDocumentsAsync(
                    new DocumentQuery(request.Skip, request.Take), ct);
                if (!result.Success) return Error(request.CorrelationId, result.ErrorMessage);
                return new WebSocketMgmtResponse
                {
                    CorrelationId = request.CorrelationId,
                    Success = true,
                    Documents = result.Value
                };
            }

            case OpStreamConstants.ManagementHubMethods.GetDocumentInfo:
            {
                RequireDocumentId(request);
                var result = await router.GetDocumentInfoAsync(request.DocumentId!, ct);
                if (!result.Success) return Error(request.CorrelationId, result.ErrorMessage);
                return new WebSocketMgmtResponse
                {
                    CorrelationId = request.CorrelationId,
                    Success = true,
                    DocumentInfo = result.Value
                };
            }

            case OpStreamConstants.ManagementHubMethods.GetSnapshot:
            {
                RequireDocumentId(request);
                var result = await router.GetSnapshotAsync(request.DocumentId!, ct);
                if (!result.Success) return Error(request.CorrelationId, result.ErrorMessage);
                return new WebSocketMgmtResponse
                {
                    CorrelationId = request.CorrelationId,
                    Success = true,
                    Snapshot = result.Value
                };
            }

            case OpStreamConstants.ManagementHubMethods.ListMilestones:
            {
                RequireDocumentId(request);
                var result = await router.ListMilestonesAsync(request.DocumentId!, ct);
                if (!result.Success) return Error(request.CorrelationId, result.ErrorMessage);
                return new WebSocketMgmtResponse
                {
                    CorrelationId = request.CorrelationId,
                    Success = true,
                    Milestones = result.Value
                };
            }

            case OpStreamConstants.ManagementHubMethods.DeleteDocument:
            {
                RequireDocumentId(request);
                var result = await router.DeleteDocumentAsync(request.DocumentId!, ct);
                if (!result.Success) return Error(request.CorrelationId, result.ErrorMessage);
                return Ok(request.CorrelationId);
            }

            case OpStreamConstants.ManagementHubMethods.CompactDocument:
            {
                RequireDocumentId(request);
                RequireRevision(request);
                var result = await router.CompactDocumentAsync(request.DocumentId!, request.UpToRevision!.Value, ct);
                if (!result.Success) return Error(request.CorrelationId, result.ErrorMessage);
                return Ok(request.CorrelationId);
            }

            case OpStreamConstants.ManagementHubMethods.PurgeHistory:
            {
                RequireDocumentId(request);
                RequireRevision(request);
                var result = await router.PurgeHistoryAsync(request.DocumentId!, request.UpToRevision!.Value, ct);
                if (!result.Success) return Error(request.CorrelationId, result.ErrorMessage);
                return Ok(request.CorrelationId);
            }

            case OpStreamConstants.ManagementHubMethods.PurgeTenant:
            {
                var result = await router.PurgeTenantAsync(ct);
                if (!result.Success) return Error(request.CorrelationId, result.ErrorMessage);
                return new WebSocketMgmtResponse
                {
                    CorrelationId = request.CorrelationId,
                    Success = true,
                    Count = result.Value
                };
            }

            default:
                return Error(request.CorrelationId, $"Unknown management command: '{request.Command}'.");
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static WebSocketMgmtResponse Ok(string? correlationId) =>
        new() { CorrelationId = correlationId, Success = true };

    private static WebSocketMgmtResponse Error(string? correlationId, string? message) =>
        new() { CorrelationId = correlationId, Success = false, ErrorMessage = message ?? "Unknown error" };

    private static void RequireDocumentId(WebSocketMgmtRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DocumentId))
            throw new InvalidOperationException($"'{nameof(request.DocumentId)}' is required for command '{request.Command}'.");
    }

    private static void RequireRevision(WebSocketMgmtRequest request)
    {
        if (request.UpToRevision is null)
            throw new InvalidOperationException($"'{nameof(request.UpToRevision)}' is required for command '{request.Command}'.");
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket webSocket, CancellationToken ct)
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
        return await reader.ReadToEndAsync(ct);
    }
}
