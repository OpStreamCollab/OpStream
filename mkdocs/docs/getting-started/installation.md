# Installation

OpStream is shipped as a set of focused NuGet packages. You only pull in
the engines, transports, and storage you actually need.

## Target frameworks

OpStream multi-targets **.NET 8.0** and **.NET 9.0**. C# 13 features are
used internally; consumers can stay on C# 10+ — only public surfaces matter.

## Minimum install

Every project needs the server package and at least one transport:

```bash
dotnet add package OpStream.Server
dotnet add package OpStream.Server.Transports.SignalR   # or WebSockets / gRPC
```

That alone gives you:

- The `TextOtEngine` registered for document type `"text"`.
- In-memory storage (**not for production**).
- The single-node `LocalBackplane` (in-process fan-out only).
- An `AllowAllAuthorizer` that grants full access (**replace before going live**).

## Pick your storage

| Backend | Package |
|---|---|
| Entity Framework Core | `OpStream.Server.Storage.EntityFrameworkCore` |
| SQL Server | `OpStream.Server.Storage.SqlServer` |
| PostgreSQL | `OpStream.Server.Storage.PostgreSQL` |
| MySQL | `OpStream.Server.Storage.MySQL` |
| SQLite | `OpStream.Server.Storage.SQLite` |
| MongoDB | `OpStream.Server.Storage.MongoDB` |
| Redis | `OpStream.Server.Storage.Redis` |

```bash
dotnet add package OpStream.Server.Storage.SqlServer
```

## Pick your transport

| Transport | Package | Best for |
|---|---|---|
| SignalR | `OpStream.Server.Transports.SignalR` | Web / Blazor / .NET clients sharing a hub |
| WebSockets | `OpStream.Server.Transports.WebSockets` | Lightweight raw-WS clients (mobile, native) |
| gRPC | `OpStream.Server.Transports.gRPC` | Service-to-service, polyglot clients |

You can register more than one — each transport gets its own endpoint.

## Going multi-node

Add the Redis backplane:

```bash
dotnet add package OpStream.Server.Backplane.Redis
```

…and call `UseRedisBackplane(...)` in your DI setup. See
[Backplane (scaling out)](../operations/backplane.md).

## Client packages

Reference one of the typed client transports from your `.NET` client app
(WPF, WinForms, MAUI, Blazor, console):

```bash
dotnet add package OpStream.Client.Transports.SignalR
# or
dotnet add package OpStream.Client.Transports.WebSockets
dotnet add package OpStream.Client.Transports.gRPC
```

For Blazor UIs we also ship optional helpers:

```bash
dotnet add package OpStream.Client.UI.Blazor
```

## Aspire integration (optional)

If you're using **.NET Aspire** for your distributed dev / deployment topology:

```bash
dotnet add package OpStream.Hosting.Aspire
```

This package provides `AddOpStream()` resources for your AppHost, including
the wired diagnostics endpoints.

## Next: [Quickstart →](quickstart.md)
