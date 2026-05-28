using Microsoft.Extensions.DependencyInjection.Extensions;
using OpStream.Client.Transports;
using OpStream.Client.Transports.gRPC;

namespace Microsoft.Extensions.DependencyInjection;

public static class OpStreamClientgRPCServiceCollectionExtensions
{
    /// <summary>
    /// Configures the client to use gRPC as transport.
    /// </summary>
    public static IOpStreamClientBuilder UsegRPCTransport(
        this IOpStreamClientBuilder builder,
        Action<OpStreamgRPCOptions> configureOptions)
    {
        builder.Services.Configure(configureOptions);
        builder.Services.TryAddTransient<IOpStreamClient, gRPCOpStreamClient>();

        return builder;
    }
}
