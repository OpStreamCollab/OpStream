# Run with Docker

**The fastest way to get OpStream running — one command, zero install.**

If you have Docker, you can have a live OpStream server in the time it takes
to read this paragraph. No .NET SDK, no NuGet restore, no project scaffolding.
Pull, run, connect.

```bash
docker run --rm -p 8080:8080 opstreamcollab/opstream:latest
```

That's it. You now have a single-node OpStream server listening on
`http://localhost:8080` with:

- **SignalR** mounted at `/collab`
- **WebSocket** endpoint at `/collab-ws`
- **In-memory** storage (ephemeral — good for tinkering)
- **Local** backplane (single node)
- **Text** + **JSON** engines registered

---

## 1. Verify it's alive

```bash
curl http://localhost:8080/health/live
# → 200 OK
```

The image also defines a Docker `HEALTHCHECK` against `/health/live`, so
`docker ps` will show the container as `(healthy)` once it's ready to accept
connections.

## 2. Connect from a browser — no .NET required

Open a browser tab to the SignalR endpoint with any SignalR JS client, or hit
the WebSocket directly:

```html
<script>
  const ws = new WebSocket("ws://localhost:8080/collab-ws");
  ws.onopen = () => ws.send(JSON.stringify({ join: "doc-42" }));
  ws.onmessage = e => console.log("from server:", e.data);
</script>
```

Open the same page in two tabs and you're collaborating. The server is .NET,
the client is **plain HTML**. See [Architecture](../architecture.md) for the
full picture of why that works.

## 3. Turn on more transports

Every transport is one env var away. Enable all three simultaneously:

```bash
docker run --rm -p 8080:8080 \
  -e OPSTREAM__TRANSPORTS="signalr,websockets,grpc" \
  -e OPSTREAM__ENGINES="text,json,rich-text,tree" \
  opstreamcollab/opstream:latest
```

The same container now serves SignalR, WebSockets **and** gRPC on the same
port. Different clients can pick different transports against the same
document.

## 4. Add real storage with docker compose

In-memory is fine for kicking the tires; switch to PostgreSQL the moment you
care about your data surviving a restart:

```yaml
# docker-compose.yml
services:
  opstream:
    image: opstreamcollab/opstream:1.0.0
    ports: ["8080:8080"]
    environment:
      OPSTREAM__TRANSPORTS: "signalr,websockets"
      OPSTREAM__STORAGE__PROVIDER: "postgres"
      OPSTREAM__STORAGE__CONNECTIONSTRING: "Host=postgres;Database=opstream;Username=opstream;Password=opstream"
    depends_on:
      postgres: { condition: service_healthy }

  postgres:
    image: postgres:16-alpine
    environment:
      POSTGRES_DB: opstream
      POSTGRES_USER: opstream
      POSTGRES_PASSWORD: opstream
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U opstream"]
      interval: 5s
      retries: 10
```

```bash
docker compose up -d
```

The EF Core migrations run on first connect — no manual `dotnet ef` step.

## 5. Pin a version for production

`latest` will move under your feet. In any environment you care about, pin to
a semantic version:

```bash
docker run -p 8080:8080 opstreamcollab/opstream:1.0.0
```

---

## When to use the Docker image vs the NuGet packages

| Use Docker when… | Use the NuGet packages when… |
|---|---|
| You want OpStream as a **standalone service** alongside your existing app | You're embedding OpStream **inside** your own ASP.NET Core process |
| Your clients are non-.NET (browser, mobile, Python, Go, …) | You need a custom `IDocumentAuthorizer` tied to your app's identity model |
| You want zero build pipeline for OpStream itself | You're writing a custom engine for a bespoke document shape |
| You're deploying with Kubernetes / Compose / Aspire | You're using `.NET Aspire` orchestration end-to-end |

Both paths produce the same wire protocol — clients can't tell the
difference. **You can switch later.**

## Next steps

<div class="grid cards" markdown>

- :material-docker: **[Full Docker reference](../operations/docker.md)**
  Every env var, every storage backend, every multi-node recipe.

- :material-sitemap: **[Architecture overview](../architecture.md)**
  How transports, engines, storage, and the backplane fit together.

- :material-rocket-launch: **[Embed in your .NET app](quickstart.md)**
  The NuGet-package path: `AddOpStream()` inside your own `Program.cs`.

- :material-school: **[Core concepts](concepts.md)**
  Documents, ops, revisions, peers — the vocabulary the rest of the docs use.

</div>
