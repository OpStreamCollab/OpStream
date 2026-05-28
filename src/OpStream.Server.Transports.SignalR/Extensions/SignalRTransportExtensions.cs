using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpStream.Server.Session;
using OpStream.Server.Transports.SignalR;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for the SignalR transport.
/// </summary>
public static class SignalRTransportExtensions
{
    /// <summary>
    /// Adds the SignalR transport to the OpStream pipeline.
    /// Multiple transports can be active simultaneously — call
    /// <c>AddWebSocketTransport()</c> or <c>AddGrpcTransport()</c> as well if needed.
    /// </summary>
    public static IOpStreamBuilder AddSignalRTransport(this IOpStreamBuilder builder)
    {
        builder.Services.AddSingleton<SignalRBackplaneRelay>();
        builder.Services.AddScoped<SignalRManagementTransport>();
        builder.Services.AddScoped<SignalRTransport>();
        builder.Services.AddSignalR();
        return builder;
    }



    /// <summary>
    /// Maps the SignalR collaboration hub to <paramref name="pattern"/>.
    /// </summary>
    public static IEndpointConventionBuilder MapOpStreamSignalR(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/collab")
    {   

        var router = endpoints.ServiceProvider.GetRequiredService<DocumentRouter>();
        // GetAwaiter().GetResult() is intentional: MapHub is synchronous and the
        // router must be ready before the first client connects.
        router.InitializeAsync().GetAwaiter().GetResult();

        // Ensure the relay singleton is instantiated so it can forward backplane events.
        endpoints.ServiceProvider.GetService<SignalRBackplaneRelay>();

        return endpoints.MapHub<SignalRTransport>(pattern);
    }

    /// <summary>
    /// Maps the SignalR management hub to <paramref name="pattern"/>.
    /// Exposes the OpStream administration surface (list / inspect / delete / compact / purge).
    /// <para>
    /// The host MUST register a real <see cref="OpStream.Shared.Abstractions.IDatabaseCommandAuthorizer"/>
    /// via <c>UseDatabaseCommandAuthorization&lt;T&gt;()</c>; otherwise every call is denied.
    /// </para>
    /// </summary>
    public static IEndpointConventionBuilder MapOpStreamSignalRManagement(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/manage")
    {
        var dbRouter = endpoints.ServiceProvider.GetRequiredService<OpStream.Server.Session.DatabaseCommandRouter>();
        // Subscribe to the cluster broadcast channel before the first client connects so
        // tenant-eviction and document-deleted messages are honoured.
        dbRouter.InitializeAsync().GetAwaiter().GetResult();

        return endpoints.MapHub<SignalRManagementTransport>(pattern);
    }
}
