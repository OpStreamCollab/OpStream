using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using OpStream.Server.Diagnostics;

namespace OpStream.Aspire;

/// <summary>
/// Aspire / OpenTelemetry integration helpers for OpStream.
/// </summary>
public static class OpStreamTelemetryExtensions
{
    /// <summary>
    /// Registers OpStream's <see cref="System.Diagnostics.ActivitySource"/> and
    /// <see cref="System.Diagnostics.Metrics.Meter"/> on the OpenTelemetry pipeline
    /// that Aspire's <c>ServiceDefaults</c> already configured.
    ///
    /// <para>Idiomatic usage in an Aspire-enabled host:</para>
    /// <code>
    /// var builder = WebApplication.CreateBuilder(args);
    /// builder.AddServiceDefaults();        // Aspire — sets up OTel exporters
    /// builder.AddOpStreamTelemetry();      // adds OpStream's source + meter
    /// </code>
    ///
    /// <para>If you are not using Aspire, this method still works: it lazily creates
    /// the OpenTelemetry pipeline via <c>AddOpenTelemetry()</c> the first time it
    /// is called, so a plain ASP.NET Core host gets the same wiring.</para>
    /// </summary>
    public static IHostApplicationBuilder AddOpStreamTelemetry(this IHostApplicationBuilder builder)
    {
        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddSource(OpStreamTelemetry.ServiceName))
            .WithMetrics(metrics => metrics.AddMeter(OpStreamTelemetry.ServiceName));

        return builder;
    }

    /// <summary>
    /// Same as <see cref="AddOpStreamTelemetry(IHostApplicationBuilder)"/> but for
    /// callers that only have access to an <see cref="IServiceCollection"/>.
    /// </summary>
    public static IServiceCollection AddOpStreamTelemetry(this IServiceCollection services)
    {
        services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddSource(OpStreamTelemetry.ServiceName))
            .WithMetrics(metrics => metrics.AddMeter(OpStreamTelemetry.ServiceName));

        return services;
    }
}
