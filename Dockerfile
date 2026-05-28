# syntax=docker/dockerfile:1.7

# ─── Build stage ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy only the manifests first so package restore can be cached when only
# source files change.
COPY src/Directory.Build.props src/Directory.Packages.props ./
COPY src/OpStream.Host/OpStream.Host.csproj OpStream.Host/
COPY src/OpStream.Server/OpStream.Server.csproj OpStream.Server/
COPY src/OpStream.Server.Transports.SignalR/OpStream.Server.Transports.SignalR.csproj OpStream.Server.Transports.SignalR/
COPY src/OpStream.Server.Transports.WebSockets/OpStream.Server.Transports.WebSockets.csproj OpStream.Server.Transports.WebSockets/
COPY src/OpStream.Server.Transports.gRPC/OpStream.Server.Transports.gRPC.csproj OpStream.Server.Transports.gRPC/
COPY src/OpStream.Server.Storage.EntityFrameworkCore/OpStream.Server.Storage.EntityFrameworkCore.csproj OpStream.Server.Storage.EntityFrameworkCore/
COPY src/OpStream.Server.Storage.PostgreSQL/OpStream.Server.Storage.PostgreSQL.csproj OpStream.Server.Storage.PostgreSQL/
COPY src/OpStream.Server.Storage.MySQL/OpStream.Server.Storage.MySQL.csproj OpStream.Server.Storage.MySQL/
COPY src/OpStream.Server.Storage.SqlServer/OpStream.Server.Storage.SqlServer.csproj OpStream.Server.Storage.SqlServer/
COPY src/OpStream.Server.Storage.SQLite/OpStream.Server.Storage.SQLite.csproj OpStream.Server.Storage.SQLite/
COPY src/OpStream.Server.Storage.MongoDB/OpStream.Server.Storage.MongoDB.csproj OpStream.Server.Storage.MongoDB/
COPY src/OpStream.Server.Storage.Redis/OpStream.Server.Storage.Redis.csproj OpStream.Server.Storage.Redis/
COPY src/OpStream.Server.Backplane.Redis/OpStream.Server.Backplane.Redis.csproj OpStream.Server.Backplane.Redis/
COPY src/OpStream.Shared.Abstractions/OpStream.Shared.Abstractions.csproj OpStream.Shared.Abstractions/
COPY src/OpStream.Shared.Messages/OpStream.Shared.Messages.csproj OpStream.Shared.Messages/
COPY src/OpStream.Constants/OpStream.Constants.csproj OpStream.Constants/
COPY src/Protos/ Protos/

RUN dotnet restore OpStream.Host/OpStream.Host.csproj

# Copy the rest of the sources and publish.
# Grpc.Tools has a Linux path-resolution bug (MSB6004) where MSBuildThisFileDirectory
# doesn't expand correctly, producing an invalid path like "/linux_x64/protoc".
# Fix: locate the tools dir at build time and pass both paths explicitly.
COPY src/ ./
RUN GRPC_TOOLS=$(find /root/.nuget/packages/grpc.tools -maxdepth 3 -name "linux_x64" -type d | head -1) && \
    GRPC_PKG_ROOT=$(dirname $(dirname "$GRPC_TOOLS")) && \
    GRPC_INCLUDE="$GRPC_PKG_ROOT/build/native/include" && \
    chmod +x "$GRPC_TOOLS/protoc" "$GRPC_TOOLS/grpc_csharp_plugin" && \
    dotnet publish OpStream.Host/OpStream.Host.csproj \
        -c Release -o /app \
        /p:UseAppHost=false \
        "/p:Protobuf_ProtocFullPath=$GRPC_TOOLS/protoc" \
        "/p:gRPC_PluginFullPath=$GRPC_TOOLS/grpc_csharp_plugin" \
        "/p:Protobuf_StandardImportsPath=$GRPC_INCLUDE"

# ─── Runtime stage ────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# Sensible defaults — override any of these at `docker run -e ...`
ENV ASPNETCORE_URLS=http://+:8080 \
    OPSTREAM__TRANSPORTS=signalr \
    OPSTREAM__ENGINES=text,json \
    OPSTREAM__STORAGE__PROVIDER=memory \
    OPSTREAM__BACKPLANE__PROVIDER=local \
    OPSTREAM__SIGNALR__PATH=/collab \
    OPSTREAM__WEBSOCKETS__PATH=/collab-ws

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD wget -qO- http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "OpStream.Host.dll"]
