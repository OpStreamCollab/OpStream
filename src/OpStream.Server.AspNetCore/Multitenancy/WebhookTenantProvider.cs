using Microsoft.AspNetCore.Http;
using OpStream.Shared.Abstractions;

namespace OpStream.Server.AspNetCore.Multitenancy
{
    /// <summary>
    /// Implementation of <see cref="ITenantProvider"/> that reads the tenant ID from the HTTP context,
    /// where it was previously stored by the <see cref="WebhookTenantMiddleware"/>.
    /// </summary>
    public class WebhookTenantProvider(IHttpContextAccessor httpContextAccessor) : ITenantProvider
    {
        /// <summary>
        /// The key used to store the resolved tenant ID in the <see cref="HttpContext.Items"/> dictionary.
        /// </summary>
        public const string HttpContextItemKey = "OpStream_ResolvedTenantId";

        /// <inheritdoc/>
        public string GetCurrentTenantId()
        {
            var context = httpContextAccessor.HttpContext;
            if (context != null && context.Items.TryGetValue(HttpContextItemKey, out var tenantId))
            {
                return tenantId?.ToString() ?? string.Empty;
            }
            return string.Empty;
        }
    }
}
