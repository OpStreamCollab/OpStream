using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpStream.Server.Backplane.Redis;
using OpStream.Server.Engine.Form;
using OpStream.Server.Engine.Json;
using OpStream.Server.Engine.RichText;
using OpStream.Server.Engine.Table;
using OpStream.Server.Engine.Tree;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration.GetSection("OpStream");

// gRPC needs HTTP/2. Allow both protocols on every Kestrel endpoint so a single
// listener can serve SignalR/WebSockets (HTTP/1.1) and gRPC (HTTP/2) cleartext.
builder.WebHost.ConfigureKestrel(k =>
    k.ConfigureEndpointDefaults(o => o.Protocols = HttpProtocols.Http1AndHttp2));

// ─── Core ────────────────────────────────────────────────────────────────────
var ops = builder.Services.AddOpStream();

// ─── Engines ─────────────────────────────────────────────────────────────────
// "text" is registered by default in AddOpStream(). Any extra engine listed here
// adds a new document type discriminator.
var engines = SplitCsv(cfg["Engines"]);
foreach (var engine in engines)
{
    switch (engine.ToLowerInvariant())
    {
        case "text":
            // Already registered by AddOpStream(); listing it is a no-op.
            break;
        case "json":
            ops.AddEngine<Json_Document, JsonOpBatch, JsonCrdtEngine>("json");
            break;
        case "rich-text":
        case "richtext":
            ops.AddEngine<RichTextDocument, RichTextOp, RichTextEngine>("rich-text");
            break;
        case "table":
            ops.AddEngine<TableDocument, TableOpBatch, TableCrdtEngine>("table");
            break;
        case "form":
            ops.AddEngine<FormDocument, FormOpBatch, FormOtEngine>("form");
            break;
        case "tree":
            ops.AddEngine<TreeDocument, TreeOpBatch, TreeCrdtEngine>("tree");
            break;
        default:
            throw new InvalidOperationException(
                $"Unknown engine '{engine}'. Valid: text, json, rich-text, table, form, tree.");
    }
}

// ─── Storage ─────────────────────────────────────────────────────────────────
var storage = (cfg["Storage:Provider"] ?? "memory").ToLowerInvariant();
switch (storage)
{
    case "memory":
        ops.UseMemoryStorage();
        break;
    case "postgres":
    case "postgresql":
        ops.UsePostgreSqlStorage(Required("Storage:ConnectionString"));
        break;
    case "mysql":
    case "mariadb":
        ops.UseMySqlStorage(Required("Storage:ConnectionString"));
        break;
    case "sqlserver":
    case "mssql":
        ops.UseSqlServerStorage(Required("Storage:ConnectionString"));
        break;
    case "sqlite":
        ops.UseSqliteStorage(Required("Storage:ConnectionString"));
        break;
    case "mongo":
    case "mongodb":
        ops.UseMongoDbStorage(
            Required("Storage:ConnectionString"),
            cfg["Storage:DatabaseName"] ?? "opstream");
        break;
    case "redis":
        ops.UseRedisStorage(Required("Storage:ConnectionString"));
        break;
    default:
        throw new InvalidOperationException(
            $"Unknown storage provider '{storage}'. Valid: memory, postgres, mysql, sqlserver, sqlite, mongo, redis.");
}

// ─── Backplane ───────────────────────────────────────────────────────────────
var backplane = (cfg["Backplane:Provider"] ?? "local").ToLowerInvariant();
switch (backplane)
{
    case "local":
        ops.UseLocalBackplane();
        break;
    case "redis":
        ops.UseRedisBackplane(Required("Backplane:ConnectionString"));
        break;
    default:
        throw new InvalidOperationException(
            $"Unknown backplane provider '{backplane}'. Valid: local, redis.");
}

// ─── Transports (register) ───────────────────────────────────────────────────
var transports = SplitCsv(cfg["Transports"]);
if (transports.Count == 0)
    throw new InvalidOperationException(
        "OPSTREAM__TRANSPORTS must list at least one of: signalr, websockets, grpc.");

var enableSignalR    = transports.Contains("signalr");
var enableWebSockets = transports.Contains("websockets");
var enableGrpc       = transports.Contains("grpc");

if (enableSignalR)    ops.AddSignalRTransport();
if (enableWebSockets) ops.AddWebSocketTransport();
if (enableGrpc)       ops.AddGrpcTransport();



// ─── Build pipeline ──────────────────────────────────────────────────────────
var app = builder.Build();

app.MapGet("/", () => Results.Text(
    $"OpStream host. Storage={storage}, Backplane={backplane}, Transports={string.Join(',', transports)}",
    "text/plain"));

// Health: /health = all checks, /health/ready = storage+backplane, /health/live = process up
app.MapHealthChecks("/health");
app.MapHealthChecks("/health/ready", new()
{
    Predicate = r => r.Tags.Contains("opstream"),
});
app.MapHealthChecks("/health/live", new()
{
    Predicate = _ => false,
});

if (enableWebSockets)
{
    app.UseWebSockets();
    app.MapOpStreamWebSockets(cfg["WebSockets:Path"] ?? "/collab-ws");
    app.MapOpStreamWebSocketsManagement(cfg["WebSockets:ManagementPath"] ?? "/manage-ws");
}

if (enableSignalR)
{
    app.MapOpStreamSignalR(cfg["SignalR:Path"] ?? "/collab");
    app.MapOpStreamSignalRManagement(cfg["SignalR:ManagementPath"] ?? "/manage");
}

if (enableGrpc)
{
    app.MapOpStreamGrpc();
    app.MapOpStreamGrpcManagement();
}

app.Run();

// ─── Helpers ─────────────────────────────────────────────────────────────────
static HashSet<string> SplitCsv(string? csv) =>
    string.IsNullOrWhiteSpace(csv)
        ? new HashSet<string>()
        : new HashSet<string>(
            csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Select(s => s.ToLowerInvariant()));

string Required(string key) =>
    cfg[key] ?? throw new InvalidOperationException(
        $"OpStream:{key} (env OPSTREAM__{key.Replace(":", "__").ToUpperInvariant()}) is required.");
