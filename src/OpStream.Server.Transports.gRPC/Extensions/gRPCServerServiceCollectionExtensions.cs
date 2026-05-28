using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpStream.Server.Session;
using OpStream.Server.Transports.gRPC;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for the gRPC streaming transport.
/// </summary>
public static class GrpcTransportExtensions
{
    /// <summary>
    /// Adds the gRPC streaming transport to the OpStream pipeline.
    /// Multiple transports can be active simultaneously — call
    /// <c>AddSignalRTransport()</c> or <c>AddWebSocketTransport()</c> as well if needed.
    /// </summary>
    public static IOpStreamBuilder AddGrpcTransport(this IOpStreamBuilder builder)
    {
        builder.Services.AddSingleton<gRPCConnectionManager>();
        builder.Services.AddSingleton<gRPCCommentConnectionManager>();
        builder.Services.AddSingleton<gRPCBackplaneRelay>();
        builder.Services.AddScoped<gRPCManagementTransport>();
        builder.Services.AddScoped<gRPCTransport>();
        builder.Services.AddScoped<gRPCCommentsTransport>();
        builder.Services.AddGrpc();
        return builder;
    }



    /// <summary>
    /// Maps the gRPC collaboration service.
    /// </summary>
    public static IEndpointConventionBuilder MapOpStreamGrpc(this IEndpointRouteBuilder endpoints)
    {
        var router = endpoints.ServiceProvider.GetRequiredService<DocumentRouter>();
        router.InitializeAsync().GetAwaiter().GetResult();

        endpoints.ServiceProvider.GetService<gRPCBackplaneRelay>();

        return endpoints.MapGrpcService<gRPCTransport>();
    }

    /// <summary>
    /// Maps the gRPC management service (<see cref="gRPCManagementTransport"/>).
    /// Exposes the OpStream administration surface (list / inspect / delete / compact / purge).
    /// <para>
    /// The host MUST register a real <see cref="OpStream.Shared.Abstractions.IDatabaseCommandAuthorizer"/>
    /// via <c>UseDatabaseCommandAuthorization&lt;T&gt;()</c>; otherwise every call is denied.
    /// </para>
    /// </summary>
    public static IEndpointConventionBuilder MapOpStreamGrpcManagement(this IEndpointRouteBuilder endpoints)
    {
        var dbRouter = endpoints.ServiceProvider.GetRequiredService<OpStream.Server.Session.DatabaseCommandRouter>();
        dbRouter.InitializeAsync().GetAwaiter().GetResult();

        return endpoints.MapGrpcService<gRPCManagementTransport>();
    }

    /// <summary>
    /// Maps the gRPC comments service (<see cref="gRPCCommentsTransport"/>).
    /// Exposes CreateComment, EditComment, ResolveComment, DeleteComment, ListOpenComments
    /// as unary RPCs, and SubscribeComments as a server-streaming RPC for real-time push.
    /// </summary>
    public static IEndpointConventionBuilder MapOpStreamGrpcComments(this IEndpointRouteBuilder endpoints)
    {
        // Ensure the comment connection manager is resolved (it's a singleton that does
        // no background work, but we resolve it here for symmetry with the relay pattern).
        endpoints.ServiceProvider.GetService<gRPCCommentConnectionManager>();

        return endpoints.MapGrpcService<gRPCCommentsTransport>();
    }
}
