# OpStream Server

[![Docker Pulls](https://img.shields.io/docker/pulls/opstreamcollab/opstream)](https://hub.docker.com/r/opstreamcollab/opstream)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-green)](https://github.com/OpStreamCollab/OpStream/blob/master/LICENSE)

Ready-to-run server image for **[OpStream](https://github.com/OpStreamCollab/OpStream)** — a real-time
synchronization framework for .NET. One image, every transport, every storage backend, every
backplane. Pick what you need with environment variables.

```bash
docker run --rm -p 8080:8080 opstreamcollab/opstream:latest
```

That's a single-node OpStream server with SignalR on `/collab`, in-memory storage, a local
backplane, and the `text` + `json` engines. Good enough to prototype against — see below
for production-grade setups.

---

## What's inside

The image bundles **every** OpStream package so you don't have to rebuild for each combination:

| Layer       | Available providers                                                  |
|-------------|----------------------------------------------------------------------|
| Transports  | `signalr`, `websockets`, `grpc`                                       |
| Engines     | `text`, `json`, `rich-text`, `table`, `form`, `tree`                 |
| Storage     | `memory`, `postgres`, `mysql`, `sqlserver`, `sqlite`, `mongo`, `redis` |
| Backplane   | `local`, `redis`                                                     |

Base image: `mcr.microsoft.com/dotnet/aspnet:9.0` (Debian 12 slim).
Exposed port: `8080` (configurable via `ASPNETCORE_URLS`).

## Tags

| Tag                | Description                                              |
|--------------------|----------------------------------------------------------|
| `latest`           | Latest stable release.                                   |
| `1.0.0`, `1.0`, `1` | Pinned semantic-version tags. **Recommended for prod.** |

Always pin to a specific version in production — `latest` will move under your feet.

## Environment variables

All settings use the `OPSTREAM__*` prefix and follow ASP.NET Core's standard config binding
(double-underscore acts as the section separator).

### Kestrel

| Variable          | Default               | Description                                            |
|-------------------|-----------------------|--------------------------------------------------------|
| `ASPNETCORE_URLS` | `http://+:8080`       | Listening URLs. Use `;` to separate multiple bindings. |

### Transports

| Variable                      | Default       | Description                                                                 |
|-------------------------------|---------------|-----------------------------------------------------------------------------|
| `OPSTREAM__TRANSPORTS`        | `signalr`     | CSV. Any combination of `signalr`, `websockets`, `grpc`. **At least one required.** |
| `OPSTREAM__SIGNALR__PATH`     | `/collab`     | SignalR hub mount point.                                                    |
| `OPSTREAM__WEBSOCKETS__PATH`  | `/collab-ws`  | WebSocket endpoint path.                                                    |

gRPC is mounted at its protobuf service path automatically — there is no configurable prefix.

### Engines

| Variable             | Default       | Description                                                                       |
|----------------------|---------------|-----------------------------------------------------------------------------------|
| `OPSTREAM__ENGINES`  | `text,json`   | CSV of document engines to register: `text`, `json`, `rich-text`, `table`, `form`, `tree`. |

The `text` engine is *always* registered by the core; listing it here is a no-op. List only
the engines your clients actually use to keep the surface tight.

### Storage

| Variable                              | Required when…              | Description                                                              |
|---------------------------------------|-----------------------------|--------------------------------------------------------------------------|
| `OPSTREAM__STORAGE__PROVIDER`         | always (defaults to `memory`) | One of `memory`, `postgres`, `mysql`, `sqlserver`, `sqlite`, `mongo`, `redis`. |
| `OPSTREAM__STORAGE__CONNECTIONSTRING` | provider ≠ `memory`         | Connection string for the chosen backend.                                |
| `OPSTREAM__STORAGE__DATABASENAME`     | provider = `mongo`          | Mongo database name (default: `opstream`).                               |

### Backplane

| Variable                                | Required when…           | Description                                                |
|-----------------------------------------|--------------------------|------------------------------------------------------------|
| `OPSTREAM__BACKPLANE__PROVIDER`         | always (defaults to `local`) | `local` (single node) or `redis` (multi-node fan-out). |
| `OPSTREAM__BACKPLANE__CONNECTIONSTRING` | provider = `redis`       | Redis connection string.                                   |

## Health checks

The image exposes three HTTP endpoints suitable for liveness/readiness probes:

| Endpoint        | Returns 200 when…                                              | Use for                  |
|-----------------|----------------------------------------------------------------|--------------------------|
| `/health/live`  | The process is running and serving HTTP.                       | Kubernetes liveness probe |
| `/health/ready` | Storage **and** backplane health checks pass.                  | Kubernetes readiness probe |
| `/health`       | All registered health checks pass (extends as you add more).    | Overall diagnostic        |

The image also defines a Docker `HEALTHCHECK` against `/health/live` out of the box.

---

## Examples

### 1. Minimal: memory + SignalR (prototyping)

```bash
docker run --rm -p 8080:8080 \
  --name opstream \
  opstreamcollab/opstream:1.0.0
```

Connect a SignalR client to `http://localhost:8080/collab`. Everything is in memory and
will vanish on container stop — ideal for kicking the tires, not for anything else.

### 2. Single node with PostgreSQL

```yaml
# docker-compose.yml
services:
  opstream:
    image: opstreamcollab/opstream:1.0.0
    ports:
      - "8080:8080"
    environment:
      OPSTREAM__TRANSPORTS: "signalr"
      OPSTREAM__ENGINES: "text,json"
      OPSTREAM__STORAGE__PROVIDER: "postgres"
      OPSTREAM__STORAGE__CONNECTIONSTRING: "Host=postgres;Port=5432;Database=opstream;Username=opstream;Password=opstream"
      OPSTREAM__BACKPLANE__PROVIDER: "local"
    depends_on:
      postgres:
        condition: service_healthy

  postgres:
    image: postgres:16-alpine
    environment:
      POSTGRES_DB: opstream
      POSTGRES_USER: opstream
      POSTGRES_PASSWORD: opstream
    volumes:
      - pgdata:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U opstream -d opstream"]
      interval: 5s
      timeout: 3s
      retries: 10

volumes:
  pgdata:
```

```bash
docker compose up -d
curl http://localhost:8080/health/ready
```

### 3. Production cluster: 3 nodes + Postgres + Redis backplane

The Redis backplane fans operations out across nodes so a client connected to **any**
instance sees updates from clients on the other two. NGINX in front gives you a single
public URL with sticky sessions (required for SignalR's WebSocket transport).

```yaml
# docker-compose.cluster.yml
services:
  opstream-1: &opstream
    image: opstreamcollab/opstream:1.0.0
    environment: &opstream-env
      OPSTREAM__TRANSPORTS: "signalr,websockets"
      OPSTREAM__ENGINES: "text,json,rich-text"
      OPSTREAM__STORAGE__PROVIDER: "postgres"
      OPSTREAM__STORAGE__CONNECTIONSTRING: "Host=postgres;Database=opstream;Username=opstream;Password=opstream"
      OPSTREAM__BACKPLANE__PROVIDER: "redis"
      OPSTREAM__BACKPLANE__CONNECTIONSTRING: "redis:6379"
    depends_on:
      postgres: { condition: service_healthy }
      redis:    { condition: service_healthy }

  opstream-2:
    <<: *opstream
    environment: *opstream-env

  opstream-3:
    <<: *opstream
    environment: *opstream-env

  nginx:
    image: nginx:1.27-alpine
    ports:
      - "80:80"
    volumes:
      - ./nginx.conf:/etc/nginx/nginx.conf:ro
    depends_on: [opstream-1, opstream-2, opstream-3]

  postgres:
    image: postgres:16-alpine
    environment:
      POSTGRES_DB: opstream
      POSTGRES_USER: opstream
      POSTGRES_PASSWORD: opstream
    volumes: [pgdata:/var/lib/postgresql/data]
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U opstream -d opstream"]
      interval: 5s
      retries: 10

  redis:
    image: redis:7-alpine
    healthcheck:
      test: ["CMD", "redis-cli", "ping"]
      interval: 5s
      retries: 10

volumes:
  pgdata:
```

`nginx.conf` (IP-hash sticky sessions so a given client stays on the same node — important
for the SignalR WebSocket transport):

```nginx
events {}
http {
  upstream opstream {
    ip_hash;
    server opstream-1:8080;
    server opstream-2:8080;
    server opstream-3:8080;
  }
  map $http_upgrade $connection_upgrade { default upgrade; '' close; }

  server {
    listen 80;
    location / {
      proxy_pass http://opstream;
      proxy_http_version 1.1;
      proxy_set_header Upgrade $http_upgrade;
      proxy_set_header Connection $connection_upgrade;
      proxy_set_header Host $host;
      proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
      proxy_read_timeout 7d;
    }
  }
}
```

### 4. SQLite with persistent volume

Great for single-tenant deployments, edge boxes, or self-hosted small teams. The DB file
lives on a named volume so it survives container restarts.

```yaml
services:
  opstream:
    image: opstreamcollab/opstream:1.0.0
    ports: ["8080:8080"]
    environment:
      OPSTREAM__TRANSPORTS: "signalr"
      OPSTREAM__STORAGE__PROVIDER: "sqlite"
      OPSTREAM__STORAGE__CONNECTIONSTRING: "Data Source=/var/lib/opstream/opstream.db;Cache=Shared"
      OPSTREAM__BACKPLANE__PROVIDER: "local"
    volumes:
      - opstream-data:/var/lib/opstream

volumes:
  opstream-data:
```

### 5. MongoDB storage

```yaml
services:
  opstream:
    image: opstreamcollab/opstream:1.0.0
    ports: ["8080:8080"]
    environment:
      OPSTREAM__TRANSPORTS: "signalr,websockets"
      OPSTREAM__STORAGE__PROVIDER: "mongo"
      OPSTREAM__STORAGE__CONNECTIONSTRING: "mongodb://opstream:opstream@mongo:27017"
      OPSTREAM__STORAGE__DATABASENAME: "opstream"
      OPSTREAM__BACKPLANE__PROVIDER: "local"
    depends_on: [mongo]

  mongo:
    image: mongo:7
    environment:
      MONGO_INITDB_ROOT_USERNAME: opstream
      MONGO_INITDB_ROOT_PASSWORD: opstream
    volumes:
      - mongo-data:/data/db

volumes:
  mongo-data:
```

### 6. All transports enabled (SignalR + WebSockets + gRPC)

Same container can serve every transport simultaneously, including HTTP/2 cleartext for
gRPC. The image already configures Kestrel to negotiate HTTP/1.1 *and* HTTP/2 on every
endpoint, so one port is enough.

```yaml
services:
  opstream:
    image: opstreamcollab/opstream:1.0.0
    ports: ["8080:8080"]
    environment:
      OPSTREAM__TRANSPORTS: "signalr,websockets,grpc"
      OPSTREAM__SIGNALR__PATH: "/collab"
      OPSTREAM__WEBSOCKETS__PATH: "/collab-ws"
      OPSTREAM__ENGINES: "text,json,table"
      OPSTREAM__STORAGE__PROVIDER: "postgres"
      OPSTREAM__STORAGE__CONNECTIONSTRING: "Host=postgres;Database=opstream;Username=opstream;Password=opstream"
      OPSTREAM__BACKPLANE__PROVIDER: "local"
    depends_on: [postgres]

  postgres:
    image: postgres:16-alpine
    environment:
      POSTGRES_DB: opstream
      POSTGRES_USER: opstream
      POSTGRES_PASSWORD: opstream
```

Endpoints:

- SignalR  → `http://host:8080/collab`
- WebSocket → `ws://host:8080/collab-ws`
- gRPC      → `host:8080` (service path defined in the `.proto`)

### 7. TLS termination with Traefik

The image only speaks plain HTTP. For TLS, terminate at the edge with Traefik, NGINX,
Caddy, or your cloud's load balancer.

```yaml
services:
  traefik:
    image: traefik:v3.1
    command:
      - --providers.docker=true
      - --entrypoints.web.address=:80
      - --entrypoints.websecure.address=:443
      - --certificatesresolvers.le.acme.email=you@example.com
      - --certificatesresolvers.le.acme.storage=/letsencrypt/acme.json
      - --certificatesresolvers.le.acme.httpchallenge.entrypoint=web
    ports: ["80:80", "443:443"]
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock:ro
      - letsencrypt:/letsencrypt

  opstream:
    image: opstreamcollab/opstream:1.0.0
    environment:
      OPSTREAM__TRANSPORTS: "signalr,websockets"
      OPSTREAM__STORAGE__PROVIDER: "memory"
      OPSTREAM__BACKPLANE__PROVIDER: "local"
    labels:
      - traefik.enable=true
      - traefik.http.routers.opstream.rule=Host(`collab.example.com`)
      - traefik.http.routers.opstream.entrypoints=websecure
      - traefik.http.routers.opstream.tls.certresolver=le
      - traefik.http.services.opstream.loadbalancer.server.port=8080

volumes:
  letsencrypt:
```

---

## Choosing a storage backend

| Provider     | Good for                                              | Notes                                                  |
|--------------|-------------------------------------------------------|--------------------------------------------------------|
| `memory`     | Local dev, tests, ephemeral demos                     | **Loses everything** on restart. Never production.     |
| `sqlite`     | Single-tenant edge boxes, small teams                 | One writer at a time; mount a volume.                  |
| `postgres`   | General-purpose, multi-tenant production              | Recommended default for new projects.                  |
| `mysql`      | Existing MySQL/MariaDB shops                          | Uses Pomelo EF Core provider.                          |
| `sqlserver`  | Existing Microsoft stacks                             | EF Core provider; works with Azure SQL.                |
| `mongo`      | Document-heavy workloads, flexible schemas            | Requires `OPSTREAM__STORAGE__DATABASENAME`.            |
| `redis`      | Lowest write latency, ephemeral or RDB-backed         | Persistence depends on Redis config.                   |

All EF Core providers ship pre-built migrations — the schema is created/upgraded on first
connect, no manual `dotnet ef` step required.

## Choosing a backplane

The backplane fans operations and presence updates out across server nodes.

- **`local`** — single-node, in-process. Fastest, zero ops cost. Use when you'll only
  ever run one instance.
- **`redis`** — multi-node fan-out via Redis pub/sub + a document-ownership map. Required
  for horizontal scaling. The same Redis instance can be used by other apps.

You do **not** need the `redis` backplane to use the Redis *storage* (and vice versa) —
they're independent knobs.

## Notes on gRPC

The image configures Kestrel with `Http1AndHttp2` on every endpoint, so a single port
serves SignalR/WebSockets (HTTP/1.1) **and** gRPC (HTTP/2 cleartext) at the same time.
No extra setup needed inside the container.

When fronting the container with a reverse proxy, make sure that proxy is HTTP/2-aware
on the upstream side if you're exposing gRPC to outside clients. NGINX needs
`grpc_pass`; Traefik handles it transparently.

## Building from source

```bash
git clone https://github.com/OpStreamCollab/OpStream.git
cd OpStream
docker build -t opstream:dev .
```

The build is a multi-stage Dockerfile: SDK image for `dotnet publish`, ASP.NET runtime
image (~220 MB) for the final layer.

## Links

- **GitHub:** https://github.com/OpStreamCollab/OpStream
- **Issues / bug reports:** https://github.com/OpStreamCollab/OpStream/issues
- **License:** MIT
