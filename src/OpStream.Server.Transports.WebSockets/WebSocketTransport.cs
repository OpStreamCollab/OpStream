using Microsoft.AspNetCore.Http;
using OpStream.Server.Multitenancy;
using OpStream.Server.Session;
using OpStream.Shared.Messages;
using System.Net.WebSockets;
using System.Reflection.Metadata;
using System.Text;
using System.Text.Json;

namespace OpStream.Server.Transports.WebSockets;

/// <summary>
/// Implements the WebSocket transport layer for the OpStream server, handling bidirectional communication with clients.
/// </summary>
public class WebSocketTransport(DocumentRouter router, WebSocketConnectionManager connectionManager, IDocumentIdGlobalizer globalizer)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Handles an incoming WebSocket request.
    /// </summary>
    /// <param name="context">The HTTP context associated with the request.</param>
    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        var peerId = Guid.NewGuid().ToString();

        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                var json = await WebSocketTransport.ReceiveTextAsync(webSocket, context.RequestAborted);
                if (json == null) break;

                var message = JsonSerializer.Deserialize<WebSocketMessage>(json, JsonOptions);
                if (message != null)
                {
                    await HandleMessage(webSocket, peerId, message);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"WebSocket error for peer {peerId}: {ex.Message}");
        }
        finally
        {
            await CleanupPeer(peerId);
            if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
            {
                try
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// Receives text data from a WebSocket connection.
    /// </summary>
    /// <param name="webSocket">The WebSocket connection to receive from.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The received text, or null if the connection was closed.</returns>
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

    /// <summary>
    /// Handles a WebSocket message from a client.
    /// </summary>
    /// <param name="webSocket">The WebSocket connection.</param>
    /// <param name="peerId">The ID of the peer sending the message.</param>
    /// <param name="message">The message to handle.</param>
    private async Task HandleMessage(WebSocket webSocket, string peerId, WebSocketMessage message)
    {
        try
        {
            switch (message.MessageType)
            {
                case WebSocketOpMessageType.JoinRequest:
                    if (message.JoinRequest != null)
                    {
                        string globalDocId = globalizer.ToGlobalId(message.JoinRequest.DocumentId);
                        var result = await router.JoinDocumentAsync(
                            globalDocId,
                            message.JoinRequest.DocumentType,
                            peerId,
                            message.JoinRequest.ClientProtoVersion);

                        if (result.Success)
                        {
                            var joinResult = result.Value!;
                            connectionManager.AddConnection(peerId, message.JoinRequest.DocumentId, webSocket);

                            var response = new WebSocketMessage
                            {
                                CorrelationId = message.CorrelationId,
                                MessageType = WebSocketOpMessageType.JoinResponse,
                                JoinResponse = new JoinResponseData(
                                    joinResult.Revision,
                                    joinResult.Snapshot.ToArray(),
                                    joinResult.CurrentAwareness)
                            };
                            await connectionManager.SendToPeerAsync(peerId, response);
                        }
                        else
                        {
                            await connectionManager.SendToPeerAsync(peerId, new WebSocketMessage 
                            { 
                                CorrelationId = message.CorrelationId,
                                MessageType = WebSocketOpMessageType.ErrorResponse,
                                ErrorMessage = result.ErrorMessage ?? "Unknown error"
                            });
                        }
                    }
                    break;

                case WebSocketOpMessageType.OpRequest:
                    if (message.OpRequest != null)
                    {
                        string globalDocId = globalizer.ToGlobalId(message.OpRequest.DocumentId);

                        var result = await router.ApplyOpAsync(
                            peerId,
                            globalDocId,
                            message.OpRequest.Payload,
                            message.OpRequest.BaseRevision);

                        if (result.Success)
                        {
                            var opResult = result.Value!;
                            var response = new WebSocketMessage
                            {
                                CorrelationId = message.CorrelationId,
                                MessageType = WebSocketOpMessageType.OpResponse,
                                OpResponse = new OpResponseData(opResult.Success, opResult.NewRevision, opResult.ErrorMessage)
                            };
                            await connectionManager.SendToPeerAsync(peerId, response);
                        }
                        else
                        {
                            await connectionManager.SendToPeerAsync(peerId, new WebSocketMessage
                            {
                                CorrelationId = message.CorrelationId,
                                MessageType = WebSocketOpMessageType.ErrorResponse,
                                ErrorMessage = result.ErrorMessage ?? "Unknown error"
                            });
                        }
                    }
                    break;

                case WebSocketOpMessageType.AwarenessRequest:
                    if (message.AwarenessRequest != null)
                    {
                        string globalDocId = globalizer.ToGlobalId(message.AwarenessRequest.DocumentId);
                        using var doc = JsonDocument.Parse(message.AwarenessRequest.DataJson);
                        await router.UpdateAwarenessAsync(peerId, globalDocId, doc.RootElement.Clone());
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            var errorResponse = new WebSocketMessage
            {
                CorrelationId = message.CorrelationId,
                MessageType = WebSocketOpMessageType.ErrorResponse,
                ErrorMessage = ex.Message
            };
            await connectionManager.SendToPeerAsync(peerId, errorResponse);
        }
    }

    /// <summary>
    /// Cleans up peer-related resources when a connection is closed.
    /// </summary>
    /// <param name="peerId">The ID of the peer to clean up.</param>
    private async Task CleanupPeer(string peerId)
    {
        await router.RemovePeerFromAllSessionsAsync(peerId);
        connectionManager.RemoveConnection(peerId);
    }
}
