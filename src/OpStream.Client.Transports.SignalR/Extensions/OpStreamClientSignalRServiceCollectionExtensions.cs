using Microsoft.Extensions.DependencyInjection.Extensions;
using OpStream.Client.Transports;
using OpStream.Client.Transports.SignalR;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for setting up OpStream SignalR client services in an <see cref="IServiceCollection" />.
/// </summary>
public static class OpStreamClientSignalRServiceCollectionExtensions
{
    /// <summary>
    /// Configures the client to use SignalR as transport.
    /// </summary>
    public static IOpStreamClientBuilder UseSignalRTransport(
        this IOpStreamClientBuilder builder,
        Action<OpStreamSignalROptions> configureOptions)
    {
        // 1. Configure the options so the developer can set the URL
        builder.Services.Configure(configureOptions);

        // 2. Register the client as TRANSIENT (or Scoped in Blazor Server). 
        // It is vital that it be Transient if you want to allow the app to connect 
        // to 2 different documents with 2 different SignalR instances at the same time.
        // Optionally we can register a Factory if you need more flexibility.
        builder.Services.TryAddTransient<IOpStreamClient, SignalROpStreamClient>();

        return builder;
    }
}

/// <summary>
/// Configuration options for the OpStream SignalR transport.
/// </summary>
public class OpStreamSignalROptions
{
    /// <summary>
    /// Gets or sets the SignalR Hub URL.
    /// </summary>
    public string HubUrl { get; set; } = "/collab";
}
