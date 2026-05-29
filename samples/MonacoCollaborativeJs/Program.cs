// Minimal self-contained host for the collaborative Monaco sample.
//
// It does three things:
//   1. Wires OpStream with the built-in "text" engine (TextOtEngine), in-memory
//      storage and the local backplane — enough for a single-node demo.
//   2. Serves the static front-end from wwwroot (index.html + the JS modules).
//   3. Maps the raw WebSocket collaboration endpoint at /collab-ws, which the
//      browser talks to directly (no Blazor, no .NET on the client).
//
// Run it and open http://localhost:5179 in two browser tabs.

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOpStream()          // registers the "text" engine by default
    .UseMemoryStorage()     // swap for UsePostgreSqlStorage(...) etc. in production
    .UseLocalBackplane()    // single node; use UseRedisBackplane(...) to scale out
    .AddWebSocketTransport();

var app = builder.Build();

app.UseDefaultFiles();      // serve index.html at "/"
app.UseStaticFiles();

app.UseWebSockets();
app.MapOpStreamWebSockets("/collab-ws");

app.Run();
