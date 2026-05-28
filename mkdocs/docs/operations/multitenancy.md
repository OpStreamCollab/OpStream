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
    public ValueTask<string> GetTenantIdAsync(CancellationToken ct = default)
    {
        var tenantId = http.HttpContext?.Request.Headers["X-Tenant-Id"].ToString();
        if (string.IsNullOrEmpty(tenantId)) throw new InvalidOperationException("Missing tenant.");
        return ValueTask.FromResult(tenantId);
    }
}
```

Register it after `AddOpStream()`:

```csharp
services.AddOpStream();
services.AddSingleton<ITenantProvider, HeaderTenantProvider>();
```

(`Add*` here because there's no fluent helper — you replace the
singleton directly. A `UseMultiTenancy<T>()` helper is on the v1.0
roadmap.)

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
    string Globalize(string tenantId, string documentId);
}
```

## See also

- [Authorization](authorization.md)
- [Storage overview](../storage/index.md) — every backend honors the
  globalized id transparently.
