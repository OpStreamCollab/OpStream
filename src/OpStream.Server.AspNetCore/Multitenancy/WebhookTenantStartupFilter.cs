using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace OpStream.Server.AspNetCore.Multitenancy
{
    /// <summary>
    /// An <see cref="IStartupFilter"/> that automatically registers the <see cref="WebhookTenantMiddleware"/>
    /// at the beginning of the application's request pipeline.
    /// </summary>
    internal class WebhookTenantStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.UseMiddleware<WebhookTenantMiddleware>();
                next(app);
            };
        }
    }
}
