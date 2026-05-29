using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpStream.Constants;
using OpStream.Shared.Abstractions;
using System.Text.Json;

namespace OpStream.Server.Session;

/// <summary>
/// The authorize → resolve-owner → run-local-or-proxy pipeline shared by every document
/// operation (join / op / awareness). It is the single place that decides <em>whether</em> a
/// caller is allowed and <em>where</em> the operation runs.
/// </summary>
public interface IDocumentExecutionPipeline
{
    /// <param name="documentId">Target document.</param>
    /// <param name="isProxied">True when the call already arrived from the owner-routing backplane.</param>
    /// <param name="permissionCheck">Predicate over the caller's <see cref="DocumentAccess"/>.</param>
    /// <param name="backplaneCommand">Backplane command type used when proxying to the owner.</param>
    /// <param name="proxyData">Payload serialized to the owner when this node is not the owner.</param>
    /// <param name="sessionProvider">Resolves the session on the owner node.</param>
    /// <param name="localAction">Runs the operation locally on the owner.</param>
    Task<OpResult<TResult>> ExecuteAsync<TResult, TRequestData>(
        string documentId,
        bool isProxied,
        Func<DocumentAccess, bool> permissionCheck,
        string backplaneCommand,
        TRequestData proxyData,
        Func<CancellationToken, Task<IDocumentSession>> sessionProvider,
        Func<IDocumentSession, CancellationToken, Task<TResult>> localAction,
        CancellationToken ct);
}

/// <inheritdoc />
public sealed class DocumentExecutionPipeline(
    IServiceScopeFactory scopeFactory,
    IBackplane backplane,
    IDocumentOwnershipManager ownershipManager,
    ILogger<DocumentExecutionPipeline> logger) : IDocumentExecutionPipeline
{
    /// <inheritdoc />
    public async Task<OpResult<TResult>> ExecuteAsync<TResult, TRequestData>(
        string documentId,
        bool isProxied,
        Func<DocumentAccess, bool> permissionCheck,
        string backplaneCommand,
        TRequestData proxyData,
        Func<CancellationToken, Task<IDocumentSession>> sessionProvider,
        Func<IDocumentSession, CancellationToken, Task<TResult>> localAction,
        CancellationToken ct)
    {
        try
        {
            if (!isProxied)
            {
                using var scope = scopeFactory.CreateScope();
                var authorizer = scope.ServiceProvider.GetRequiredService<IDocumentAuthorizer>();
                var access = await authorizer.AuthorizeAsync(documentId, ct);
                if (!permissionCheck(access))
                    return OpResult<TResult>.Fail("Forbidden: Insufficient permissions for this operation.");
            }

            var ownerNodeId = await ownershipManager.GetOrAcquireOwnerAsync(documentId, backplane.NodeId, ct);

            if (ownerNodeId == backplane.NodeId)
            {
                var session = await sessionProvider(ct);
                return OpResult<TResult>.Ok(await localAction(session, ct));
            }

            var response = await backplane.SendRequestAsync(
                ownerNodeId,
                backplaneCommand,
                JsonSerializer.SerializeToUtf8Bytes(proxyData, OpStreamJsonOptions.Default),
                ct);

            if (!response.Success)
                return OpResult<TResult>.Fail(response.ErrorMessage ?? "Remote node execution failed");

            var value = JsonSerializer.Deserialize<TResult>(response.Payload.Span, OpStreamJsonOptions.Default)!;
            return OpResult<TResult>.Ok(value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in execution pipeline for document {DocId} command {Command}", documentId, backplaneCommand);
            return OpResult<TResult>.Fail(ex.Message);
        }
    }
}
