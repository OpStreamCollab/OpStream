# OpStream.Hosting.Aspire

AppHost-side helpers for OpStream.

> **Status:** scaffolding. The Aspire AppHost SDK (`Aspire.Hosting.AppHost`)
> is currently distributed under a preview channel and its API surface
> still evolves per release. To avoid version-locking the OpStream main
> repository to a specific Aspire preview, this package today only ships
> the canonical resource-name constants (`OpStreamResourceNames`) that the
> AppHost and the consumer services agree on.

## Intended usage (when the AppHost reference lands)

```csharp
// AppHost/Program.cs
var builder = DistributedApplication.CreateBuilder(args);

var redis = builder.AddRedis(OpStreamResourceNames.Redis);
var db    = builder.AddPostgres("pg")
                   .AddDatabase(OpStreamResourceNames.RelationalDatabase);

builder.AddProject<Projects.MyWebApp>("webapi")
       .WithReference(redis)
       .WithReference(db);

builder.Build().Run();
```

```csharp
// MyWebApp/Program.cs
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();        // Aspire ServiceDefaults
builder.AddOpStreamTelemetry();      // OpStream.Aspire

builder.Services.AddOpStream()
    .UseRedisStorage(builder.Configuration[OpStreamResourceNames.RedisConnectionStringKey]!)
    .UseRedisBackplane(builder.Configuration[OpStreamResourceNames.RedisConnectionStringKey]!)
    .UseSqlServerStorage(builder.Configuration[OpStreamResourceNames.RelationalConnectionStringKey]!);
```

## Why this package is currently minimal

Pinning `Aspire.Hosting.AppHost` requires choosing one Aspire version (9.0,
9.1, …). Doing that in the main OpStream tree would couple every release of
OpStream to one Aspire preview. Instead, this package gives consumers the
*names* (`OpStreamResourceNames.Redis`, `OpStreamResourceNames.RelationalDatabase`,
`OpStreamResourceNames.RedisConnectionStringKey`, …) so AppHost code and
service code share a single source of truth. Strongly-typed AppHost
extension methods can be added once the Aspire SDK API stabilises.
