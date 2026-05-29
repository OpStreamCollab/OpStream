using Microsoft.Extensions.DependencyInjection.Extensions;
using OpStream.Client.Transports;
using OpStream.Client.Transports.SignalR;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for setting up OpStream SignalR client services in an <see cref="IServiceCollection" />.
/// </summary>
public static class OpStreamClientSignalRServiceCollectionExtensions
{
    /// <summary>
    /// Configures the collaboration client to use SignalR as transport.
    /// </summary>
    public static IOpStreamClientBuilder UseSignalRTransport(
        this IOpStreamClientBuilder builder,
        Action<OpStreamSignalROptions> configureOptions)
    {
        builder.Services.Configure(configureOptions);
        builder.Services.TryAddTransient<IOpStreamClient, SignalROpStreamClient>();
        return builder;
    }

    /// <summary>
    /// Registers <see cref="SignalROpStreamManagementClient"/> as <see cref="IOpStreamManagementClient"/>.
    /// </summary>
    public static IOpStreamClientBuilder UseSignalRManagementTransport(
        this IOpStreamClientBuilder builder,
        Action<OpStreamSignalRManagementOptions> configureOptions)
    {
        builder.Services.Configure(configureOptions);
        builder.Services.TryAddTransient<IOpStreamManagementClient>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OpStreamSignalRManagementOptions>>().Value;
            return new SignalROpStreamManagementClient(opts.ManagementHubUrl, opts.VersioningHubUrl);
        });
        return builder;
    }
}

/// <summary>Configuration options for the OpStream SignalR collaboration transport.</summary>
public class OpStreamSignalROptions
{
    /// <summary>Gets or sets the SignalR collaboration hub URL.</summary>
    public string HubUrl { get; set; } = "/collab";
}

/// <summary>Configuration options for the OpStream SignalR management transport.</summary>
public class OpStreamSignalRManagementOptions
{
    /// <summary>Gets or sets the management hub URL.</summary>
    public string ManagementHubUrl { get; set; } = "/mgmt";

    /// <summary>Gets or sets the versioning hub URL.</summary>
    public string VersioningHubUrl { get; set; } = "/versioning";
}
