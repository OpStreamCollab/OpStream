using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpStream.Constants;
using OpStream.Server.Multitenancy;
using OpStream.Server.Session;
using OpStream.Server.Validation;
using OpStream.Shared.Abstractions;
using System.Text.Json;

namespace OpStream.Server.Comments;

/// <summary>
/// Routes comment mutations (create / edit / resolve / delete) through the same authorization
/// + tenant globalization + owner-routing plumbing used by <see cref="DocumentRouter"/>.
/// <para>
/// Read commands resolve locally against <see cref="ICommentStore"/> because storage is shared
/// across nodes. Mutating commands are routed to the owner node so the new anchor can be pinned
/// to <see cref="IDocumentSession.CurrentRevision"/> atomically (under the session lock).
/// </para>
/// </summary>
public class CommentRouter(
    IServiceScopeFactory scopeFactory,
    IBackplane backplane,
    IDocumentOwnershipManager ownershipManager,
    IDocumentIdGlobalizer globalizer,
    DocumentRouter documentRouter,
    ICommentStore store,
    IInboundMessageValidationPipeline inboundValidation,
    IAwarenessSessionRegistry awarenessRegistry,
    ILogger<CommentRouter> logger) : IBackplaneRequestExtension
{
    /// <summary>
    /// Exposed for the transport layer: lets relays expose the underlying comment store for
    /// read-only paths (e.g. backplane-driven cache invalidation).
    /// </summary>
    internal ICommentStore Store => store;

    // ─── Public API used by transports ───────────────────────────────────────

    public async Task<OpResult<IReadOnlyList<Comment>>> ListOpenAsync(string localDocumentId, CancellationToken ct = default)
    {
        var validation = await inboundValidation.ValidateAsync(
            new InboundMessage(InboundMessageKind.CommentList, PeerId: string.Empty, localDocumentId), ct);
        if (!validation.IsValid)
            return OpResult<IReadOnlyList<Comment>>.Fail($"InvalidMessage: {validation.Reason}");

        if (!await AuthorizeAsync(localDocumentId, requireCanComment: false, ct))
            return Forbidden<IReadOnlyList<Comment>>();

        var globalId = globalizer.ToGlobalId(localDocumentId);
        var list = await store.LoadOpenAsync(globalId, ct);
        // Hide the global prefix on the way out.
        var projected = list.Select(c => c with { DocumentId = localDocumentId }).ToList();
        return OpResult<IReadOnlyList<Comment>>.Ok(projected);
    }

    public async Task<OpResult<Comment>> CreateAsync(string peerId, string localDocumentId, NewCommentCmd cmd, CancellationToken ct = default)
    {
        var validation = await inboundValidation.ValidateAsync(
            new InboundMessage(InboundMessageKind.CommentCreate, peerId, localDocumentId, Text: cmd?.Body), ct);
        if (!validation.IsValid)
            return OpResult<Comment>.Fail($"InvalidMessage: {validation.Reason}");

        return await RouteToOwnerAsync<Comment>(
            localDocumentId, peerId,
            OpStreamConstants.BackplaneCommands.CreateComment,
            new MutationPayload(MutationKind.Create, peerId, null, cmd, null),
            ct);
    }

    public async Task<OpResult<Comment>> EditAsync(string peerId, string localDocumentId, string commentId, string newBody, CancellationToken ct = default)
    {
        var validation = await inboundValidation.ValidateAsync(
            new InboundMessage(InboundMessageKind.CommentEdit, peerId, localDocumentId, Text: newBody), ct);
        if (!validation.IsValid)
            return OpResult<Comment>.Fail($"InvalidMessage: {validation.Reason}");

        return await RouteToOwnerAsync<Comment>(
            localDocumentId, peerId,
            OpStreamConstants.BackplaneCommands.EditComment,
            new MutationPayload(MutationKind.Edit, peerId, commentId, null, newBody),
            ct);
    }

    public async Task<OpResult<Comment>> ResolveAsync(string peerId, string localDocumentId, string commentId, CancellationToken ct = default)
    {
        var validation = await inboundValidation.ValidateAsync(
            new InboundMessage(InboundMessageKind.CommentResolve, peerId, localDocumentId), ct);
        if (!validation.IsValid)
            return OpResult<Comment>.Fail($"InvalidMessage: {validation.Reason}");

        return await RouteToOwnerAsync<Comment>(
            localDocumentId, peerId,
            OpStreamConstants.BackplaneCommands.ResolveComment,
            new MutationPayload(MutationKind.Resolve, peerId, commentId, null, null),
            ct);
    }

    public async Task<OpResult> DeleteAsync(string peerId, string localDocumentId, string commentId, CancellationToken ct = default)
    {
        var validation = await inboundValidation.ValidateAsync(
            new InboundMessage(InboundMessageKind.CommentDelete, peerId, localDocumentId), ct);
        if (!validation.IsValid)
            return OpResult.Fail($"InvalidMessage: {validation.Reason}");

        var routed = await RouteToOwnerAsync<bool>(
            localDocumentId, peerId,
            OpStreamConstants.BackplaneCommands.DeleteComment,
            new MutationPayload(MutationKind.Delete, peerId, commentId, null, null),
            ct);
        return routed.Success ? OpResult.Ok() : OpResult.Fail(routed.ErrorMessage ?? "Unknown error");
    }

    // ─── Owner-routing engine ────────────────────────────────────────────────

    private async Task<OpResult<TResult>> RouteToOwnerAsync<TResult>(
        string localDocumentId,
        string peerId,
        string backplaneCommand,
        MutationPayload payload,
        CancellationToken ct)
    {
        try
        {
            if (!await AuthorizeAsync(localDocumentId, requireCanComment: true, ct))
                return OpResult<TResult>.Fail("Forbidden: Insufficient permissions for this operation.");

            var globalId = globalizer.ToGlobalId(localDocumentId);
            payload = payload with { GlobalDocumentId = globalId };

            var ownerNodeId = await ownershipManager.GetOrAcquireOwnerAsync(globalId, backplane.NodeId, ct);
            if (ownerNodeId == backplane.NodeId)
            {
                return await ExecuteLocallyAsync<TResult>(payload, ct);
            }

            var response = await backplane.SendRequestAsync(
                ownerNodeId,
                backplaneCommand,
                JsonSerializer.SerializeToUtf8Bytes(payload, OpStreamJsonOptions.Default),
                ct);

            if (!response.Success)
                return OpResult<TResult>.Fail(response.ErrorMessage ?? "Remote node execution failed");

            if (response.Payload.IsEmpty)
                return OpResult<TResult>.Ok(default!);

            var value = JsonSerializer.Deserialize<TResult>(response.Payload.Span, OpStreamJsonOptions.Default)!;
            return OpResult<TResult>.Ok(value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error routing comment mutation {Kind} for {DocId}", payload.Kind, localDocumentId);
            return OpResult<TResult>.Fail(ex.Message);
        }
    }

    private async Task<OpResult<TResult>> ExecuteLocallyAsync<TResult>(MutationPayload payload, CancellationToken ct)
    {
        var globalId = payload.GlobalDocumentId!;
        switch (payload.Kind)
        {
            case MutationKind.Create:
            {
                var cmd = payload.Cmd ?? throw new InvalidOperationException("Create requires Cmd.");
                if (cmd.ParentCommentId is null && cmd.Anchor is null)
                    return OpResult<TResult>.Fail("Root comments require an Anchor.");
                if (cmd.ParentCommentId is not null && cmd.Anchor is not null)
                    return OpResult<TResult>.Fail("Reply comments must not carry an Anchor.");

                // Snapshot CurrentRevision under the session lock so the anchor reflects the
                // exact doc state when the comment was attached. Falls back to 0 when there is
                // no live session (e.g. comments on a doc nobody is currently editing).
                long anchoredAt = await SnapshotRevisionAsync(globalId, ct);

                var authorName = ResolveAuthorName(globalId, payload.PeerId);

                var comment = new Comment(
                    Id: Guid.NewGuid().ToString("N"),
                    DocumentId: globalId,
                    ParentCommentId: cmd.ParentCommentId,
                    AuthorPeerId: payload.PeerId,
                    AuthorName: authorName,
                    Body: cmd.Body,
                    Anchor: cmd.Anchor,
                    AnchoredAtRevision: anchoredAt,
                    CreatedAt: DateTimeOffset.UtcNow,
                    ResolvedAt: null,
                    ResolvedByPeerId: null,
                    IsOrphaned: false);

                await store.AddAsync(comment, ct);
                await PublishAsync(globalId, OpStreamConstants.BackplaneMessages.CommentCreated, comment, payload.PeerId, ct);
                return OpResult<TResult>.Ok((TResult)(object)comment);
            }

            case MutationKind.Edit:
            {
                var existing = await store.GetAsync(payload.CommentId!, ct);
                if (existing is null) return OpResult<TResult>.Fail("Comment not found.");
                if (!string.Equals(existing.DocumentId, globalId, StringComparison.Ordinal))
                    return OpResult<TResult>.Fail("Comment does not belong to this document.");

                var updated = existing with { Body = payload.NewBody ?? existing.Body };
                await store.UpdateAsync(updated, ct);
                await PublishAsync(globalId, OpStreamConstants.BackplaneMessages.CommentUpdated, updated, payload.PeerId, ct);
                return OpResult<TResult>.Ok((TResult)(object)updated);
            }

            case MutationKind.Resolve:
            {
                var existing = await store.GetAsync(payload.CommentId!, ct);
                if (existing is null) return OpResult<TResult>.Fail("Comment not found.");
                if (!string.Equals(existing.DocumentId, globalId, StringComparison.Ordinal))
                    return OpResult<TResult>.Fail("Comment does not belong to this document.");

                var resolved = existing with
                {
                    ResolvedAt = DateTimeOffset.UtcNow,
                    ResolvedByPeerId = payload.PeerId
                };
                await store.UpdateAsync(resolved, ct);
                await PublishAsync(globalId, OpStreamConstants.BackplaneMessages.CommentUpdated, resolved, payload.PeerId, ct);
                return OpResult<TResult>.Ok((TResult)(object)resolved);
            }

            case MutationKind.Delete:
            {
                var existing = await store.GetAsync(payload.CommentId!, ct);
                if (existing is null) return OpResult<TResult>.Fail("Comment not found.");
                if (!string.Equals(existing.DocumentId, globalId, StringComparison.Ordinal))
                    return OpResult<TResult>.Fail("Comment does not belong to this document.");

                await store.DeleteAsync(payload.CommentId!, ct);
                await PublishAsync(globalId, OpStreamConstants.BackplaneMessages.CommentDeleted,
                    new DeletedCommentMsg(payload.CommentId!, globalId), payload.PeerId, ct);
                return OpResult<TResult>.Ok((TResult)(object)true);
            }

            default:
                return OpResult<TResult>.Fail($"Unsupported comment mutation: {payload.Kind}");
        }
    }

    private async Task<long> SnapshotRevisionAsync(string globalDocumentId, CancellationToken ct)
    {
        // Best-effort: if there is no live session we just use 0 (the comment will be rebased
        // forward as ops are applied later). If there IS one, we grab CurrentRevision under
        // its lock so no concurrent op can sneak past us.
        foreach (var docId in documentRouter.GetActiveDocumentIds())
        {
            if (!string.Equals(docId, globalDocumentId, StringComparison.Ordinal)) continue;
            var session = documentRouter.TryGetActiveSession(globalDocumentId);
            if (session is null) break;
            return await session.ExecuteUnderLockAsync(rev => ValueTask.FromResult(rev), ct);
        }
        return 0;
    }

    private async Task PublishAsync(string globalDocumentId, string messageType, object payload, string senderPeerId, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, OpStreamJsonOptions.Default);
        await backplane.PublishAsync(globalDocumentId, new BackplaneMessage(
            backplane.NodeId, messageType, bytes, senderPeerId), ct);
    }

    // ─── IBackplaneRequestExtension ──────────────────────────────────────────

    public bool CanHandle(string type) => type switch
    {
        OpStreamConstants.BackplaneCommands.CreateComment  => true,
        OpStreamConstants.BackplaneCommands.EditComment    => true,
        OpStreamConstants.BackplaneCommands.ResolveComment => true,
        OpStreamConstants.BackplaneCommands.DeleteComment  => true,
        _ => false
    };

    public async Task<BackplaneResponse> HandleAsync(BackplaneRequest request, CancellationToken ct = default)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<MutationPayload>(request.Payload.Span, OpStreamJsonOptions.Default)!;
            if (request.Type == OpStreamConstants.BackplaneCommands.DeleteComment)
            {
                var result = await ExecuteLocallyAsync<bool>(payload, ct);
                return result.Success
                    ? new BackplaneResponse(request.RequestId, true, ReadOnlyMemory<byte>.Empty)
                    : new BackplaneResponse(request.RequestId, false, ReadOnlyMemory<byte>.Empty, result.ErrorMessage);
            }
            else
            {
                var result = await ExecuteLocallyAsync<Comment>(payload, ct);
                return result.Success
                    ? new BackplaneResponse(request.RequestId, true,
                        JsonSerializer.SerializeToUtf8Bytes(result.Value, OpStreamJsonOptions.Default))
                    : new BackplaneResponse(request.RequestId, false, ReadOnlyMemory<byte>.Empty, result.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling comment backplane request {RequestId} of type {Type}", request.RequestId, request.Type);
            return new BackplaneResponse(request.RequestId, false, ReadOnlyMemory<byte>.Empty, ex.Message);
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async ValueTask<bool> AuthorizeAsync(string localDocumentId, bool requireCanComment, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var authorizer = scope.ServiceProvider.GetRequiredService<IDocumentAuthorizer>();
        var globalId = globalizer.ToGlobalId(localDocumentId);
        var access = await authorizer.AuthorizeAsync(globalId, ct);
        return requireCanComment ? access.CanComment : access.CanRead;
    }

    private static OpResult<T> Forbidden<T>() => OpResult<T>.Fail("Forbidden: Insufficient permissions for this operation.");

    /// <summary>
    /// Tries to resolve the human-readable display name of the author from the current
    /// awareness session. Falls back to "Anonymous" if the peer has no live presence data.
    /// </summary>
    private string ResolveAuthorName(string globalDocumentId, string peerId)
    {
        try
        {
            var awarenessSession = awarenessRegistry.TryGet(globalDocumentId);
            if (awarenessSession is null) return "Anonymous";

            var state = awarenessSession.GetStates()
                .FirstOrDefault(s => string.Equals(s.PeerId, peerId, StringComparison.Ordinal));

            if (state is null || state.Data.ValueKind == System.Text.Json.JsonValueKind.Undefined)
                return "Anonymous";

            // The awareness payload shape is { name, color, ... } (camelCase from clients).
            if (state.Data.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == System.Text.Json.JsonValueKind.String)
                return nameProp.GetString() ?? "Anonymous";

            // Some clients may send Name (PascalCase).
            if (state.Data.TryGetProperty("Name", out var nameCapProp) && nameCapProp.ValueKind == System.Text.Json.JsonValueKind.String)
                return nameCapProp.GetString() ?? "Anonymous";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not resolve author name for peer {PeerId}", peerId);
        }

        return "Anonymous";
    }

    private enum MutationKind { Create, Edit, Resolve, Delete }

    private record MutationPayload(
        MutationKind Kind,
        string PeerId,
        string? CommentId,
        NewCommentCmd? Cmd,
        string? NewBody,
        string? GlobalDocumentId = null);

    private record DeletedCommentMsg(string CommentId, string DocumentId);
}
