using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpStream.Constants;
using OpStream.Shared.Abstractions;
using System.Text.Json;

namespace OpStream.Server.Session;

// Shared wire payloads for owner-routed operations. Built by DocumentRouter when proxying and
// deserialized here when this node is the owner that received the proxied request.
internal sealed record JoinRequestData(string DocumentId, string DocumentType, string PeerId, int ProtocolVersion);
internal sealed record ApplyOpRequestData(string DocumentId, string PeerId, byte[] Payload, long BaseRevision);
internal sealed record UpdateAwarenessRequestData(string DocumentId, string PeerId, JsonElement Data);

/// <summary>
/// Handles the inbound side of the backplane: registers the request handler and dispatches
/// owner-routed join / op / awareness requests (and the management/comment extension commands)
/// to their handlers. The collaboration requests are dispatched back into <see cref="DocumentRouter"/>
/// with <c>isProxied: true</c>.
/// </summary>
public interface IDocumentBackplaneGateway
{
    /// <summary>Registers the inbound request handler with the backplane. Idempotent per node.</summary>
    Task StartAsync(CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class DocumentBackplaneGateway(
    IServiceProvider serviceProvider,
    IBackplane backplane,
    ILogger<DocumentBackplaneGateway> logger) : IDocumentBackplaneGateway
{
    // Resolved lazily to break the dependency cycle:
    // DocumentRouter → gateway → DocumentRouter / IBackplaneRequestExtension(→ DocumentRouter).
    private DocumentRouter Router => serviceProvider.GetRequiredService<DocumentRouter>();
    private IReadOnlyList<IBackplaneRequestExtension>? _extensions;
    private IReadOnlyList<IBackplaneRequestExtension> Extensions =>
        _extensions ??= serviceProvider.GetServices<IBackplaneRequestExtension>().ToArray();

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct = default)
        => backplane.RegisterRequestHandlerAsync(HandleIncomingRequestAsync, ct);

    private async Task<BackplaneResponse> HandleIncomingRequestAsync(BackplaneRequest request)
    {
        try
        {
            switch (request.Type)
            {
                case OpStreamConstants.BackplaneCommands.JoinDocument:
                {
                    var data = Deserialize<JoinRequestData>(request);
                    var result = await Router.JoinDocumentAsync(
                        data.DocumentId, data.DocumentType, data.PeerId, data.ProtocolVersion, isProxied: true);
                    return ToResponse(request, result.Success, result.ErrorMessage, result.Value);
                }
                case OpStreamConstants.BackplaneCommands.ApplyOp:
                {
                    var data = Deserialize<ApplyOpRequestData>(request);
                    var result = await Router.ApplyOpAsync(
                        data.PeerId, data.DocumentId, data.Payload, data.BaseRevision, isProxied: true);
                    return ToResponse(request, result.Success, result.ErrorMessage, result.Value);
                }
                case OpStreamConstants.BackplaneCommands.UpdateAwareness:
                {
                    var data = Deserialize<UpdateAwarenessRequestData>(request);
                    var result = await Router.UpdateAwarenessAsync(
                        data.PeerId, data.DocumentId, data.Data, isProxied: true);
                    return ToResponse(request, result.Success, result.ErrorMessage, result.Value);
                }
                default:
                {
                    foreach (var extension in Extensions)
                    {
                        if (extension.CanHandle(request.Type))
                            return await extension.HandleAsync(request);
                    }
                    return new BackplaneResponse(request.RequestId, false, ReadOnlyMemory<byte>.Empty, $"Unknown request type: {request.Type}");
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling incoming backplane request {RequestId} of type {Type}", request.RequestId, request.Type);
            return new BackplaneResponse(request.RequestId, false, ReadOnlyMemory<byte>.Empty, ex.Message);
        }
    }

    private static T Deserialize<T>(BackplaneRequest request)
        => JsonSerializer.Deserialize<T>(request.Payload.Span, OpStreamJsonOptions.Default)!;

    private static BackplaneResponse ToResponse<T>(BackplaneRequest request, bool success, string? error, T value)
        => success
            ? new BackplaneResponse(request.RequestId, true, JsonSerializer.SerializeToUtf8Bytes(value, OpStreamJsonOptions.Default))
            : new BackplaneResponse(request.RequestId, false, ReadOnlyMemory<byte>.Empty, error);
}
