using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using OpStream.Server.Comments;
using OpStream.Shared.Messages.gRPC;
using System.Text.Json;

namespace OpStream.Server.Transports.gRPC;

/// <summary>
/// gRPC service that exposes the OpStream comments surface
/// (CreateComment, EditComment, ResolveComment, DeleteComment, ListOpenComments, SubscribeComments).
/// All mutating calls are routed through <see cref="CommentRouter"/>.
/// </summary>
public class gRPCCommentsTransport(
    CommentRouter commentRouter,
    gRPCCommentConnectionManager commentConnections) : OpStreamCommentsService.OpStreamCommentsServiceBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public override async Task<CommentResponse> CreateComment(
        CreateCommentRequest request, ServerCallContext context)
    {
        var peerId = request.PeerId;

        Anchor? anchor = null;
        if (!string.IsNullOrEmpty(request.AnchorJson))
        {
            try
            {
                anchor = System.Text.Json.JsonSerializer.Deserialize<Anchor>(
                    request.AnchorJson, JsonOptions);
            }
            catch
            {
                return CommentFail("Invalid anchor JSON.");
            }
        }

        var cmd = new NewCommentCmd(
            request.Body,
            anchor,
            string.IsNullOrEmpty(request.ParentCommentId) ? null : request.ParentCommentId);

        var result = await commentRouter.CreateAsync(peerId, request.DocumentId, cmd, context.CancellationToken);
        return result.Success
            ? CommentOk(result.Value)
            : CommentFail(result.ErrorMessage);
    }

    public override async Task<CommentResponse> EditComment(
        EditCommentRequest request, ServerCallContext context)
    {
        var result = await commentRouter.EditAsync(
            request.PeerId, request.DocumentId, request.CommentId, request.NewBody, context.CancellationToken);
        return result.Success ? CommentOk(result.Value) : CommentFail(result.ErrorMessage);
    }

    public override async Task<CommentResponse> ResolveComment(
        CommentActionRequest request, ServerCallContext context)
    {
        var result = await commentRouter.ResolveAsync(
            request.PeerId, request.DocumentId, request.CommentId, context.CancellationToken);
        return result.Success ? CommentOk(result.Value) : CommentFail(result.ErrorMessage);
    }

    public override async Task<CommentOkResponse> DeleteComment(
        CommentActionRequest request, ServerCallContext context)
    {
        var result = await commentRouter.DeleteAsync(
            request.PeerId, request.DocumentId, request.CommentId, context.CancellationToken);
        return result.Success
            ? new CommentOkResponse { Success = true }
            : new CommentOkResponse { Success = false, ErrorMessage = result.ErrorMessage ?? "Unknown error" };
    }

    public override async Task<ListCommentsResponse> ListOpenComments(
        ListCommentsRequest request, ServerCallContext context)
    {
        var result = await commentRouter.ListOpenAsync(request.DocumentId, context.CancellationToken);
        if (!result.Success)
            throw new RpcException(new Status(StatusCode.Internal, result.ErrorMessage ?? "Unknown error"));

        var response = new ListCommentsResponse();
        foreach (var c in result.Value!)
            response.Comments.Add(ToProto(c));
        return response;
    }

    public override async Task SubscribeComments(
        SubscribeCommentsRequest request,
        IServerStreamWriter<CommentEvent> responseStream,
        ServerCallContext context)
    {
        commentConnections.Register(request.DocumentId, responseStream);
        try
        {
            // Hold the call open until the client disconnects.
            await Task.Delay(Timeout.Infinite, context.CancellationToken);
        }
        catch (OperationCanceledException) { }
        finally
        {
            commentConnections.Unregister(request.DocumentId, responseStream);
        }
    }

    // ─── Mapping ─────────────────────────────────────────────────────────────

    internal static CommentProto ToProto(Comment c) => new()
    {
        Id = c.Id,
        DocumentId = c.DocumentId,
        ParentCommentId = c.ParentCommentId ?? string.Empty,
        AuthorPeerId = c.AuthorPeerId,
        Body = c.Body,
        AnchorJson = c.Anchor is null
            ? string.Empty
            : System.Text.Json.JsonSerializer.Serialize(c.Anchor, JsonOptions),
        AnchoredAtRevision = c.AnchoredAtRevision,
        CreatedAt = Timestamp.FromDateTimeOffset(c.CreatedAt),
        ResolvedAt = c.ResolvedAt.HasValue ? Timestamp.FromDateTimeOffset(c.ResolvedAt.Value) : null,
        ResolvedByPeerId = c.ResolvedByPeerId ?? string.Empty,
        IsOrphaned = c.IsOrphaned
    };

    private static CommentResponse CommentOk(Comment? c) => new()
    {
        Success = true,
        Comment = c is null ? null : ToProto(c)
    };

    private static CommentResponse CommentFail(string? message) => new()
    {
        Success = false,
        ErrorMessage = message ?? "Unknown error"
    };
}
