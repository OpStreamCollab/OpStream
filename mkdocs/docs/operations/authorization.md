# Authorization

OpStream delegates **all** document-level access decisions to your app
via `IDocumentAuthorizer`. There is no built-in role model, no
hard-coded ACL — only an extension point.

## The contract

```csharp
public interface IDocumentAuthorizer
{
    ValueTask<DocumentAccess> AuthorizeAsync(string documentId, CancellationToken ct = default);
}

public sealed record DocumentAccess(bool CanRead, bool CanWrite)
{
    public static DocumentAccess None       => new(false, false);
    public static DocumentAccess ReadOnly   => new(true, false);
    public static DocumentAccess ReadWrite() => new(true, true);
}
```

The authorizer is resolved **per request** with `Scoped` lifetime, so it
can take an `IHttpContextAccessor`, the current `ClaimsPrincipal`, or
any other request-bound dependency.

## Default — and the warning

`AddOpStream()` registers an `AllowAllAuthorizer` that grants
`ReadWrite()` to everyone. The router logs a warning at startup when
it's still active:

```
OpStream is running with the AllowAllAuthorizer. Every join / op /
awareness call is accepted. Wire your own IDocumentAuthorizer via
UseAuthorization<T>() before production.
```

## Wiring your own

```csharp
public sealed class TenantDocumentAuthorizer : IDocumentAuthorizer
{
    private readonly IHttpContextAccessor _http;
    private readonly IPermissionService _perms;

    public TenantDocumentAuthorizer(IHttpContextAccessor http, IPermissionService perms)
        => (_http, _perms) = (http, perms);

    public async ValueTask<DocumentAccess> AuthorizeAsync(string documentId, CancellationToken ct = default)
    {
        var user = _http.HttpContext?.User;
        if (user is null) return DocumentAccess.None;

        var perms = await _perms.GetForAsync(user, documentId, ct);
        return new DocumentAccess(perms.CanRead, perms.CanWrite);
    }
}
```

```csharp
services
    .AddOpStream()
    .UseAuthorization<TenantDocumentAuthorizer>();
```

`UseAuthorization<T>()` registers your type with **Scoped** lifetime and
replaces the default. Calling it again replaces it again — `Use*` is
singleton-style.

## When the authorizer runs

| Call | Permission required |
|---|---|
| `JoinDocumentAsync` | `CanRead` |
| `ApplyOpAsync` | `CanWrite` |
| `UpdateAwarenessAsync` | `CanRead` |

Authorization runs **once per call** on the origin node. Proxied calls
between cluster nodes skip authorization — the origin already approved
the request.

## Multi-tenancy

When OpStream is multi-tenant (see [Multi-tenancy](multitenancy.md)),
the `documentId` passed to your authorizer is the **caller-supplied**
id, not the tenant-globalized one. Your authorizer should resolve the
tenant from the request context (e.g. via `ITenantProvider`) and check
its own ACL accordingly.

## Patterns

### Owner / Editor / Viewer

```csharp
public async ValueTask<DocumentAccess> AuthorizeAsync(string documentId, CancellationToken ct = default)
{
    var role = await _acl.GetRoleAsync(_user, documentId, ct);
    return role switch
    {
        DocRole.Owner  => DocumentAccess.ReadWrite(),
        DocRole.Editor => DocumentAccess.ReadWrite(),
        DocRole.Viewer => DocumentAccess.ReadOnly,
        _              => DocumentAccess.None,
    };
}
```

### Share links

Treat the share-token as a scoped ClaimsPrincipal:

```csharp
public async ValueTask<DocumentAccess> AuthorizeAsync(string documentId, CancellationToken ct = default)
{
    var token = _http.HttpContext?.Request.Query["share"].ToString();
    if (!string.IsNullOrEmpty(token))
    {
        var link = await _shareLinks.ResolveAsync(token, documentId, ct);
        if (link is not null) return new DocumentAccess(true, link.CanEdit);
    }
    return DocumentAccess.None;
}
```

### Read-only audit users

A claim or group membership flips `CanWrite` off but keeps `CanRead`.
The client receives the snapshot and live ops but `SendOp` calls are
rejected with `Forbidden`.

## See also

- [Multi-tenancy](multitenancy.md) — how `documentId` is namespaced per tenant.
- [Builder API: UseAuthorization](../reference/builder-api.md#useauthorization).
