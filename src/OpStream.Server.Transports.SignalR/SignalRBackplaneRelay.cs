using Microsoft.AspNetCore.SignalR;
using OpStream.Constants;
using OpStream.Server.Session;
using OpStream.Shared.Abstractions;
using OpStream.Shared.Messages;
using System.Text.Json;

namespace OpStream.Server.Transports.SignalR;

/// <summary>
/// Relays messages from the backplane to SignalR connected clients.
/// </summary>
public class SignalRBackplaneRelay
{
    private readonly IHubContext<SignalRTransport> _hubContext;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="SignalRBackplaneRelay"/> class.
    /// </summary>
    /// <param name="router">The document router to subscribe to backplane messages.</param>
    /// <param name="hubContext">The SignalR hub context for broadcasting messages.</param>
    public SignalRBackplaneRelay(DocumentRouter router, IHubContext<SignalRTransport> hubContext)
    {
        _hubContext = hubContext;
        router.OnBackplaneMessage += HandleBackplaneMessageAsync;
    }

    /// <summary>
    /// Handles backplane messages and broadcasts them to the appropriate SignalR clients.
    /// </summary>
    /// <param name="documentId">The ID of the document associated with the message.</param>
    /// <param name="message">The backplane message to process.</param>
    private async Task HandleBackplaneMessageAsync(string documentId, BackplaneMessage message)
    {
        switch (message.Type)
        {
            case OpStreamConstants.BackplaneMessages.OpApplied:
                var clients = message.SenderPeerId != null 
                    ? _hubContext.Clients.GroupExcept(documentId, message.SenderPeerId) 
                    : _hubContext.Clients.Group(documentId);

                var opPayload = JsonSerializer.Deserialize<OpAppliedBackplanePayload>(message.Payload.Span, JsonOptions);
                if (opPayload != null)
                {
                    await clients.SendAsync(OpStreamConstants.ClientEvents.ReceiveOp, opPayload.Operation, opPayload.NewRevision);
                }
                break;

            case OpStreamConstants.BackplaneMessages.ReceiveAwarenessUpdate:
                var awarenessClients = message.SenderPeerId != null 
                    ? _hubContext.Clients.GroupExcept(documentId, message.SenderPeerId) 
                    : _hubContext.Clients.Group(documentId);
                
                var update = JsonSerializer.Deserialize<AwarenessState>(message.Payload.Span, JsonOptions);
                await awarenessClients.SendAsync(OpStreamConstants.ClientEvents.ReceiveAwarenessUpdate, update);
                break;

            case OpStreamConstants.BackplaneMessages.PeerDisconnected:
                await _hubContext.Clients.Group(documentId).SendAsync(OpStreamConstants.ClientEvents.PeerDisconnected, message.SenderPeerId);
                break;
        }
    }
}
