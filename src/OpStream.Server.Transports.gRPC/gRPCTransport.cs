using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using OpStream.Server.Multitenancy;
using OpStream.Server.Session;
using OpStream.Shared.Messages;
using OpStream.Shared.Messages.gRPC;
using System.Reflection.Metadata;
using System.Text.Json;

namespace OpStream.Server.Transports.gRPC;

/// <summary>
/// Implements the gRPC transport layer for the OpStream server, handling bidirectional communication with clients.
/// </summary>
public class gRPCTransport(DocumentRouter router, gRPCConnectionManager connectionManager, IDocumentIdGlobalizer globalizer) : OpStreamService.OpStreamServiceBase
{
    /// <summary>
    /// Establishes a bidirectional stream between the client and the server.
    /// </summary>
    /// <param name="requestStream">The stream of incoming client messages.</param>
    /// <param name="responseStream">The stream of outgoing server messages.</param>
    /// <param name="context">The server call context.</param>
    public override async Task Connect(IAsyncStreamReader<ClientMessage> requestStream, IServerStreamWriter<ServerMessage> responseStream, ServerCallContext context)
    {
        var peerId = Guid.NewGuid().ToString();

        try
        {
            while (await requestStream.MoveNext())
            {
                var message = requestStream.Current;
                var correlationId = message.CorrelationId;

                try
                {
                    switch (message.MessageTypeCase)
                    {
                        case ClientMessage.MessageTypeOneofCase.JoinRequest:
                            await HandleJoinRequest(message.JoinRequest, correlationId, responseStream, peerId, context.CancellationToken);
                            break;
                        case ClientMessage.MessageTypeOneofCase.OpRequest:
                            await HandleOpRequest(message.OpRequest, correlationId, peerId, context.CancellationToken);
                            break;
                        case ClientMessage.MessageTypeOneofCase.AwarenessRequest:
                            await HandleAwarenessRequest(message.AwarenessRequest, correlationId, peerId, context.CancellationToken);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    await responseStream.WriteAsync(new ServerMessage
                    {
                        CorrelationId = correlationId,
                        ErrorResponse = new ErrorResponse { Message = ex.Message }
                    });
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in gRPC connection {peerId}: {ex}");
        }
        finally
        {
            await CleanupPeer(peerId);
        }
    }

    /// <summary>
    /// Handles a join request from a client.
    /// </summary>
    /// <param name="request">The join request data.</param>
    /// <param name="correlationId">The correlation ID for the request.</param>
    /// <param name="responseStream">The outgoing response stream.</param>
    /// <param name="peerId">The ID of the peer joining.</param>
    /// <param name="ct">The cancellation token.</param>
    private async Task HandleJoinRequest(JoinRequest request, string correlationId, IServerStreamWriter<ServerMessage> responseStream, string peerId, CancellationToken ct)
    {
        string globalDocId = globalizer.ToGlobalId(request.DocumentId);

        var result = await router.JoinDocumentAsync(globalDocId, request.DocumentType, peerId, request.ClientProtoVersion, ct: ct);

        if (result.Success)
        {
            var joinResult = result.Value!;
            connectionManager.AddConnection(peerId, globalDocId, responseStream);

            var response = new ServerMessage
            {
                CorrelationId = correlationId,
                JoinResponse = new JoinResponse
                {
                    Revision = joinResult.Revision,
                    Snapshot = Google.Protobuf.ByteString.CopyFrom(joinResult.Snapshot.Span)
                }
            };

            foreach (var state in joinResult.CurrentAwareness)
            {
                response.JoinResponse.Awareness.Add(ToProto(state));
            }

            await connectionManager.SendToPeerAsync(peerId, response);
        }
        else
        {
            await responseStream.WriteAsync(new ServerMessage
            {
                CorrelationId = correlationId,
                ErrorResponse = new ErrorResponse { Message = result.ErrorMessage ?? "Unknown error" }
            });
        }
    }

    /// <summary>
    /// Handles an operation request from a client.
    /// </summary>
    /// <param name="request">The operation request data.</param>
    /// <param name="correlationId">The correlation ID for the request.</param>
    /// <param name="peerId">The ID of the peer sending the operation.</param>
    /// <param name="ct">The cancellation token.</param>
    private async Task HandleOpRequest(OpRequest request, string correlationId, string peerId, CancellationToken ct)
    {
        string globalDocId = globalizer.ToGlobalId(request.DocumentId);

        var result = await router.ApplyOpAsync(peerId, globalDocId, request.Payload.ToByteArray(), request.BaseRevision,ct: ct);

        if (result.Success)
        {
            var opResult = result.Value!;
            var response = new ServerMessage
            {
                CorrelationId = correlationId,
                OpResponse = new OpResponse
                {
                    Success = opResult.Success,
                    NewRevision = opResult.NewRevision,
                    ErrorMessage = opResult.ErrorMessage
                }
            };

            await connectionManager.SendToPeerAsync(peerId, response);
        }
        else
        {
            // gRPC usually returns errors via trailers or a dedicated field in the message.
            // Since we have a stream, we can send an ErrorResponse if the proto allows it, or just use OpResponse with Success=false.
            var response = new ServerMessage
            {
                CorrelationId = correlationId,
                OpResponse = new OpResponse
                {
                    Success = false,
                    ErrorMessage = result.ErrorMessage
                }
            };
            await connectionManager.SendToPeerAsync(peerId, response);
        }
    }

    /// <summary>
    /// Handles an awareness update request from a client.
    /// </summary>
    /// <param name="request">The awareness request data.</param>
    /// <param name="correlationId">The correlation ID for the request.</param>
    /// <param name="peerId">The ID of the peer sending the awareness update.</param>
    /// <param name="ct">The cancellation token.</param>
    private async Task HandleAwarenessRequest(AwarenessRequest request, string correlationId, string peerId, CancellationToken ct)
    {
        string globalDocId = globalizer.ToGlobalId(request.DocumentId);

        using var doc = JsonDocument.Parse(request.DataJson);
        await router.UpdateAwarenessAsync(peerId, globalDocId, doc.RootElement.Clone(),ct: ct);
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

    /// <summary>
    /// Converts an <see cref="AwarenessState"/> object to its gRPC protobuf representation.
    /// </summary>
    /// <param name="state">The awareness state to convert.</param>
    /// <returns>The protobuf representation of the awareness state.</returns>
    private static AwarenessStateProto ToProto(AwarenessState state)
    {
        return new AwarenessStateProto
        {
            PeerId = state.PeerId,
            DataJson = state.Data.GetRawText(),
            LastUpdated = Timestamp.FromDateTimeOffset(state.LastUpdated)
        };
    }
}
