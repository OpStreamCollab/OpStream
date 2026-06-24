using System;

namespace OpStream.Server.AspNetCore.Multitenancy
{
    /// <summary>
    /// Options for configuring the WebhookTenantProvider.
    /// </summary>
    public class WebhookTenantOptions
    {
        /// <summary>
        /// Gets or sets the URL of the webhook to call for tenant resolution.
        /// </summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the name of the HTTP header from the original request 
        /// that will be forwarded to the webhook (e.g., "Authorization", "X-Api-Key").
        /// Default is "Authorization".
        /// </summary>
        public string TokenHeaderName { get; set; } = "Authorization";

        /// <summary>
        /// Gets or sets the timeout for the webhook HTTP request.
        /// Default is 5 seconds.
        /// </summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
    }
}
