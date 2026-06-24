# Multi-tenancy

OpStream supports running multiple isolated tenants on a single server.
The document id your client sends is **globalized** behind the scenes so
two tenants can use the same `documentId` value without collision.

## Default tenant provider

`AddOpStream()` registers a `DefaultTenantProvider` that returns a
single tenant id (`"default"`). Single-tenant deployments don't need to
touch this — the globalized id is just `default:doc-1`.

## Wiring per-request tenant resolution

```csharp
public sealed class HeaderTenantProvider(IHttpContextAccessor http) : ITenantProvider
{
    public string GetCurrentTenantId()
    {
        var tenantId = http.HttpContext?.Request.Headers["X-Tenant-Id"].ToString();
        if (string.IsNullOrEmpty(tenantId)) throw new InvalidOperationException("Missing tenant.");
        return tenantId;
    }
}
```

Register it after `AddOpStream()`:

```csharp
services.AddOpStream();
services.AddSingleton<ITenantProvider, HeaderTenantProvider>();
```

## Webhook Tenant Provider (Opt-in)

If you host OpStream in an ASP.NET Core application, you can use the built-in webhook multitenancy feature. This allows OpStream to delegate tenant resolution to an external HTTP service, making your primary app framework-agnostic. 

First, install the `OpStream.Server.AspNetCore` package, which provides the webhook extensions.

```xml
<PackageReference Include="OpStream.Server.AspNetCore" Version="1.0.0" />
```

Then, configure the URL of your webhook in your `appsettings.json`:

```json
{
  "OpStream": {
    "TenantWebhook": {
      "Url": "https://api.my-backend.com/internal/resolve-tenant",
      "TokenHeaderName": "Authorization",
      "Timeout": "00:00:05"
    }
  }
}
```

Finally, wire up the configuration in your `Program.cs` by passing the `IConfiguration` to `AddOpStream`:

```csharp
builder.Services.AddOpStream(builder.Configuration);
```

When OpStream detects the `Url` in the configuration, it automatically:
1. Replaces `DefaultTenantProvider` with `WebhookTenantProvider`.
2. Registers a non-blocking, asynchronous middleware that intercepts OpStream connections.
3. Forwards the token (from headers or query string) to your webhook.
4. Reads the JSON or text response to resolve the tenant ID before processing the document operation.

## How globalization works

The `IDocumentIdGlobalizer` (default: `TenantAwareDocumentIdGlobalizer`)
combines the resolved tenant id with the caller-supplied document id:

```
"default" + "doc-1"        →  "default:doc-1"
"acme"    + "doc-1"        →  "acme:doc-1"
"globex"  + "blocks/page-7" →  "globex:blocks/page-7"
```

Storage keys, backplane channels, ownership leases — all use the
globalized id. Your application code keeps using the **un-globalized**
id at the API boundary:

| Layer | Sees |
|---|---|
| Client transport | `"doc-1"` |
| `IDocumentAuthorizer` | `"doc-1"` (un-globalized — you read the tenant yourself) |
| `IDocumentStore` | `"acme:doc-1"` |
| `IBackplane` | `"acme:doc-1"` |

## Authorization in multi-tenant apps

Your authorizer is the obvious place to verify tenant scoping:

```csharp
public async ValueTask<DocumentAccess> AuthorizeAsync(string documentId, CancellationToken ct = default)
{
    var tenantId = _user.FindFirstValue("tenant_id");
    var allowedTenant = await _tenantOfDoc.GetAsync(documentId, ct);

    if (tenantId != allowedTenant) return DocumentAccess.None;
    // ... per-document checks ...
}
```

## Custom globalizers

If your tenant model needs different namespacing (per-region, per-product
line, …), implement `IDocumentIdGlobalizer` and register it as a
singleton. The interface is:

```csharp
public interface IDocumentIdGlobalizer
{
    string ToGlobalId(string localDocumentId);
    string ToLocalId(string globalDocumentId);
    string GetCurrentTenantPrefix();
}
```

## See also

- [Authorization](authorization.md)
- [Storage overview](../storage/index.md) — every backend honors the
  globalized id transparently.
