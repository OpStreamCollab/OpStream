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
        builder.Services.AddSingleton<gRPCBackplaneRelay>();
        builder.Services.AddGrpc();
        return builder;
    }

    /// <inheritdoc cref="AddGrpcTransport"/>
    [Obsolete("Use AddGrpcTransport() instead. This alias will be removed in v1.0.", error: false)]
    public static IOpStreamBuilder AddOpStreamgRPCTransport(this IOpStreamBuilder builder)
        => builder.AddGrpcTransport();

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
}
