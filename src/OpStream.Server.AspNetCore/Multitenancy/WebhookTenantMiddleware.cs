using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpStream.Server.AspNetCore.Multitenancy
{
    /// <summary>
    /// Middleware that intercepts incoming requests, queries an external webhook to resolve the tenant ID,
    /// and stores it in the HTTP context for the <see cref="WebhookTenantProvider"/> to read synchronously.
    /// </summary>
    public class WebhookTenantMiddleware(
        RequestDelegate next,
        IHttpClientFactory httpClientFactory,
        IOptions<WebhookTenantOptions> options,
        ILogger<WebhookTenantMiddleware> logger)
    {
        private class WebhookResponse
        {
            public string? TenantId { get; set; }
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var opts = options.Value;

            if (string.IsNullOrEmpty(opts.Url))
            {
                await next(context);
                return;
            }

            // Optimization: only act on OpStream endpoints. 
            // In ASP.NET Core, if we are in the middleware pipeline, we might not have the endpoint matched yet,
            // but we can check the path or let the webhook run. It's safer to just let it run or check common paths.
            // For now, we will run it on every request. If the host has other APIs, they might want the tenant anyway.
            
            try
            {
                var token = ExtractToken(context, opts.TokenHeaderName);
                var tenantId = await ResolveTenantIdAsync(opts, token, context.RequestAborted);
                
                if (!string.IsNullOrEmpty(tenantId))
                {
                    context.Items[WebhookTenantProvider.HttpContextItemKey] = tenantId;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to resolve tenant ID from webhook.");
            }

            await next(context);
        }

        private static string? ExtractToken(HttpContext context, string headerName)
        {
            var token = context.Request.Headers[headerName].FirstOrDefault();
            
            if (string.IsNullOrEmpty(token))
            {
                // Fallback for WebSockets / SignalR which often use query parameters for tokens
                token = context.Request.Query["access_token"].FirstOrDefault();
            }

            if (string.IsNullOrEmpty(token))
            {
                token = context.Request.Query[headerName].FirstOrDefault();
            }

            return token;
        }

        private async Task<string> ResolveTenantIdAsync(WebhookTenantOptions opts, string? token, System.Threading.CancellationToken cancellationToken)
        {
            var client = httpClientFactory.CreateClient("TenantWebhook");
            client.Timeout = opts.Timeout;

            var request = new HttpRequestMessage(HttpMethod.Get, opts.Url);
            
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.TryAddWithoutValidation(opts.TokenHeaderName, token);
            }

            var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(content))
            {
                return string.Empty;
            }

            var trimmedContent = content.Trim();
            if (trimmedContent.StartsWith("{"))
            {
                try
                {
                    var json = JsonSerializer.Deserialize<WebhookResponse>(trimmedContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (json?.TenantId != null)
                    {
                        return json.TenantId;
                    }
                }
                catch
                {
                    // Ignore JSON parse errors, fallback to returning the whole string
                }
            }

            // Fallback: assume the endpoint returns plain text
            // Just return the first line or up to a reasonable length to avoid huge strings
            return trimmedContent.Length > 200 ? trimmedContent.Substring(0, 200) : trimmedContent;
        }
    }
}
