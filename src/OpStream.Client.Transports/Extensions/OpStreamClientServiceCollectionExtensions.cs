using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpStream.Client.Transports;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Builder for configuring OpStream client services.
/// </summary>
public interface IOpStreamClientBuilder
{
    /// <summary>
    /// Gets the service collection.
    /// </summary>
    IServiceCollection Services { get; }
}

internal class OpStreamClientBuilder : IOpStreamClientBuilder
{
    public IServiceCollection Services { get; }
    public OpStreamClientBuilder(IServiceCollection services) => Services = services;




}

public static class OpStreamClientServiceCollectionExtensions
{
    /// <summary>
    /// Configures the necessary services for a client (Blazor, MAUI) 
    /// to connect to an OpStream server.
    /// </summary>
    public static IOpStreamClientBuilder AddOpStreamClient(this IServiceCollection services)
    {
        return new OpStreamClientBuilder(services);
    }
}


