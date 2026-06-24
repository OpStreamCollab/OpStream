using System;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpStream.Server.AspNetCore.Multitenancy;
using OpStream.Shared.Abstractions;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>
    /// Extension methods for configuring webhook-based multitenancy in OpStream using ASP.NET Core features.
    /// </summary>
    public static class OpStreamWebhookTenantExtensions
    {
        /// <summary>
        /// Registers the OpStream server services using an <see cref="IConfiguration"/> object.
        /// If the configuration contains a valid URL under the "OpStream:TenantWebhook:Url" key, 
        /// it automatically replaces the default tenant provider with the webhook tenant provider.
        /// </summary>
        public static IOpStreamBuilder AddOpStream(
            this IServiceCollection services,
            IConfiguration configuration,
            Action<OpStream.Server.Models.OpStreamOptions>? configure = null)
        {
            var builder = services.AddOpStream(configure);

            var webhookUrl = configuration["OpStream:TenantWebhook:Url"];
            if (!string.IsNullOrEmpty(webhookUrl))
            {
                builder.UseWebhookTenantProvider(options =>
                {
                    options.Url = webhookUrl;
                    
                    var headerName = configuration["OpStream:TenantWebhook:TokenHeaderName"];
                    if (!string.IsNullOrEmpty(headerName))
                    {
                        options.TokenHeaderName = headerName;
                    }
                    
                    if (TimeSpan.TryParse(configuration["OpStream:TenantWebhook:Timeout"], out var timeout))
                    {
                        options.Timeout = timeout;
                    }
                });
            }

            return builder;
        }

        /// <summary>
        /// Replaces the default <see cref="ITenantProvider"/> with <see cref="WebhookTenantProvider"/>,
        /// which resolves tenant IDs by querying an external webhook asynchronously.
        /// </summary>
        public static IOpStreamBuilder UseWebhookTenantProvider(
            this IOpStreamBuilder builder,
            Action<WebhookTenantOptions> configureOptions)
        {
            // 1. Remove any existing tenant providers (like DefaultTenantProvider)
            builder.Services.RemoveAll<ITenantProvider>();

            // 2. Add the synchronous provider that reads from the HttpContext
            builder.Services.AddSingleton<ITenantProvider, WebhookTenantProvider>();

            // 3. Register IHttpContextAccessor (required to read the items from the request pipeline)
            builder.Services.AddHttpContextAccessor();

            // 4. Configure options and the named HttpClient for the middleware
            builder.Services.Configure(configureOptions);
            builder.Services.AddHttpClient("TenantWebhook");

            // 5. Register the StartupFilter to inject the asynchronous middleware automatically
            builder.Services.AddTransient<IStartupFilter, WebhookTenantStartupFilter>();

            return builder;
        }
    }
}
