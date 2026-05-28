using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpStream.Server.Session;
using OpStream.Server.Transports.WebSockets;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for the raw WebSocket transport.
/// </summary>
public static class WebSocketTransportExtensions
{
    /// <summary>
    /// Adds the WebSocket transport to the OpStream pipeline.
    /// Multiple transports can be active simultaneously — call
    /// <c>AddSignalRTransport()</c> or <c>AddGrpcTransport()</c> as well if needed.
    /// </summary>
    public static IOpStreamBuilder AddWebSocketTransport(this IOpStreamBuilder builder)
    {
        builder.Services.AddSingleton<WebSocketConnectionManager>();
        builder.Services.AddSingleton<WebSocketBackplaneRelay>();
        builder.Services.AddScoped<WebSocketTransport>();
        builder.Services.AddScoped<WebSocketManagementTransport>();
        builder.Services.AddScoped<WebSocketCommentsTransport>();
        return builder;
    }



    // AddWebSocketTransport also registers WebSocketManagementTransport because management is
    // an optional endpoint on the same listener — no separate AddXxx call required.

    /// <summary>
    /// Maps the WebSocket collaboration endpoint to <paramref name="pattern"/>.
    /// </summary>
    public static IEndpointConventionBuilder MapOpStreamWebSockets(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/collab-ws")
    {
        var router = endpoints.ServiceProvider.GetRequiredService<DocumentRouter>();
        router.InitializeAsync().GetAwaiter().GetResult();

        endpoints.ServiceProvider.GetService<WebSocketBackplaneRelay>();

        return endpoints.Map(pattern, async context =>
        {
            var transport = context.RequestServices.GetRequiredService<WebSocketTransport>();
            await transport.HandleAsync(context);
        });
    }

    /// <summary>
    /// Maps the WebSocket management endpoint to <paramref name="pattern"/>.
    /// Exposes the OpStream administration surface (list / inspect / delete / compact / purge).
    /// <para>
    /// The host MUST register a real <see cref="OpStream.Shared.Abstractions.IDatabaseCommandAuthorizer"/>
    /// via <c>UseDatabaseCommandAuthorization&lt;T&gt;()</c>; otherwise every call is denied.
    /// </para>
    /// </summary>
    public static IEndpointConventionBuilder MapOpStreamWebSocketsManagement(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/manage-ws")
    {
        var dbRouter = endpoints.ServiceProvider.GetRequiredService<OpStream.Server.Session.DatabaseCommandRouter>();
        dbRouter.InitializeAsync().GetAwaiter().GetResult();

        return endpoints.Map(pattern, async context =>
        {
            var transport = context.RequestServices.GetRequiredService<WebSocketManagementTransport>();
            await transport.HandleAsync(context);
        });
    }

    /// <summary>
    /// Maps the WebSocket comments endpoint to <paramref name="pattern"/>.
    /// Exposes CreateComment, EditComment, ResolveComment, DeleteComment, and ListOpenComments
    /// over the same JSON-envelope protocol used by the management transport.
    /// Server-push events (CommentCreated / CommentUpdated / CommentDeleted) are delivered
    /// to all document peers via <see cref="WebSocketBackplaneRelay"/>.
    /// </summary>
    public static IEndpointConventionBuilder MapOpStreamWebSocketsComments(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/comments-ws")
    {
        return endpoints.Map(pattern, async context =>
        {
            var transport = context.RequestServices.GetRequiredService<WebSocketCommentsTransport>();
            await transport.HandleAsync(context);
        });
    }
}
