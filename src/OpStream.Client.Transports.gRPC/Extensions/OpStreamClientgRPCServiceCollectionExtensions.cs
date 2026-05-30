using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpStream.Client.Transports;
using OpStream.Client.Transports.gRPC;

namespace Microsoft.Extensions.DependencyInjection;

public static class OpStreamClientgRPCServiceCollectionExtensions
{
    /// <summary>
    /// Configures the collaboration client to use gRPC as transport.
    /// </summary>
    public static IOpStreamClientBuilder UsegRPCTransport(
        this IOpStreamClientBuilder builder,
        Action<OpStreamgRPCOptions> configureOptions)
    {
        builder.Services.Configure(configureOptions);
        builder.Services.TryAddTransient<IOpStreamClient, gRPCOpStreamClient>();
        return builder;
    }

    /// <summary>
    /// Registers <see cref="gRPCOpStreamManagementClient"/> as <see cref="IOpStreamManagementClient"/>.
    /// </summary>
    public static IOpStreamClientBuilder UsegRPCManagementTransport(
        this IOpStreamClientBuilder builder,
        Action<OpStreamgRPCManagementOptions> configureOptions)
    {
        builder.Services.Configure(configureOptions);
        builder.Services.TryAddTransient<IOpStreamManagementClient>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<OpStreamgRPCManagementOptions>>().Value;
            return new gRPCOpStreamManagementClient(opts.Address);
        });
        return builder;
    }
}

/// <summary>Configuration options for the gRPC management transport.</summary>
public class OpStreamgRPCManagementOptions
{
    /// <summary>gRPC server address (e.g., https://hostdemo.opstream.stream/).</summary>
    public string Address { get; set; } = "https://hostdemo.opstream.stream/";
}
