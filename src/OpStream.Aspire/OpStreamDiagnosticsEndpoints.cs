using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpStream.Server.Session;

namespace OpStream.Aspire;

/// <summary>
/// Endpoint-mapping extensions for the OpStream diagnostics surface.
/// </summary>
public static class OpStreamDiagnosticsEndpoints
{
    /// <summary>
    /// Maps the diagnostics endpoint:
    /// <list type="bullet">
    ///   <item><c>GET {basePath}/{docId}</c> — returns a <see cref="OpStream.Server.Diagnostics.DocumentDiagnostics"/> JSON document
    ///         (current revision, peers, last N ops, owner node).</item>
    ///   <item><c>GET {basePath}/health</c> — returns the live health-check verdict.</item>
    /// </list>
    /// <para>
    /// Always gate this endpoint with an authorization policy: it exposes the
    /// peer list and the tail of the op log, which is sensitive.
    /// </para>
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="basePath">Base path (default <c>/opstream/diag</c>).</param>
    /// <param name="authorizationPolicy">
    /// Name of an authorization policy that must succeed (e.g. <c>"OpStreamDiagnostics"</c>).
    /// When null, the endpoint is anonymous — only use for local development.
    /// </param>
    /// <param name="recentOpCount">Number of recent ops to include per document (default 50).</param>
    public static IEndpointRouteBuilder MapOpStreamDiagnostics(
        this IEndpointRouteBuilder endpoints,
        string basePath = "/opstream/diag",
        string? authorizationPolicy = null,
        int recentOpCount = 50)
    {
        var docGroup = endpoints.MapGroup(basePath);

        if (!string.IsNullOrWhiteSpace(authorizationPolicy))
            docGroup.RequireAuthorization(authorizationPolicy);

        // GET /opstream/diag/{docId}
        docGroup.MapGet("/{docId}", async (string docId, HttpContext ctx) =>
        {
            var router = ctx.RequestServices.GetRequiredService<DocumentRouter>();
            var snap = await router.GetDiagnosticsSnapshotAsync(docId, recentOpCount, ctx.RequestAborted);
            return Results.Json(snap);
        })
        .WithName("OpStreamDiagDocument")
        .WithTags("OpStream.Diagnostics");

        return endpoints;
    }
}
