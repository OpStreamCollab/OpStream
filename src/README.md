# OpStream

**OpStream** es un framework de sincronización en tiempo real para .NET diseñado para construir aplicaciones colaborativas escalables. Permite la sincronización de flujos de operaciones (Operational Streams) entre múltiples clientes y servidores con soporte nativo para diversos protocolos de transporte y motores de almacenamiento.

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)
![License](https://img.shields.io/badge/license-MIT-green)
![Aspire](https://img.shields.io/badge/Aspire-Integrated-blue)

## 🚀 Características Principales

- **Multi-Transporte**: Soporte nativo para **gRPC**, **WebSockets** y **SignalR**. Elige el protocolo que mejor se adapte a tu latencia y requisitos de red.
- **Multi-Almacenamiento**: Persistencia flexible con implementaciones para:
  - **Relacionales**: PostgreSQL, MySQL, SQL Server, SQLite (vía EF Core).
  - **NoSQL**: MongoDB.
  - **Key-Value**: Redis.
  - **En Memoria**: Para desarrollo y pruebas rápidas.
- **Escalabilidad Horizontal (Backplane)**: Sincronización entre múltiples nodos de servidor utilizando **Redis** o comunicación local.
- **Integración con .NET Aspire**: Orquestación simplificada, telemetría (OpenTelemetry) y health checks integrados.
- **Awareness (Presencia)**: Sistema integrado para compartir estados efímeros como cursores, "quién está escribiendo" o estado de conexión.
- **Arquitectura de Motores**: Soporte para diferentes tipos de documentos (JSON, texto plano, etc.) mediante motores extensibles.

## 🏗️ Estructura del Proyecto

El repositorio sigue una arquitectura modular y limpia:

- **`OpStream.Server`**: El núcleo del motor de sincronización.
- **`OpStream.Client.Transports.*`**: Implementaciones de clientes para gRPC, SignalR y WebSockets.
- **`OpStream.Server.Storage.*`**: Adaptadores de almacenamiento para diferentes bases de datos.
- **`OpStream.Server.Transports.*`**: Implementaciones de servidor para los protocolos soportados.
- **`OpStream.Shared.Abstractions`**: Contratos e interfaces base.
- **`OpStream.Aspire`**: Extensiones para telemetría y hosting en .NET Aspire.

## 🛠️ Configuración Rápida

### Servidor (Web API)

Configurar un servidor OpStream es sencillo gracias a su API fluida en el `Program.cs`:

```csharp
var opStreamBuilder = builder.Services
    .AddOpStream()
    // Añade el motor para documentos JSON
    .AddEngine<Json_Document, JsonOpBatch, JsonCrdtEngine>("json");

// Configurar almacenamiento dinámicamente
opStreamBuilder.UsePostgreSqlStorage(connectionString);

// Configurar Backplane para escalado horizontal
opStreamBuilder.UseRedisBackplane(redisConnectionString);

// Habilitar transportes
opStreamBuilder.AddSignalRTransport();
opStreamBuilder.AddWebSocketTransport();
opStreamBuilder.AddGrpcTransport();

// ... en el pipeline de middleware
app.MapOpStreamSignalR("/collab");
app.MapOpStreamWebSockets("/collab-ws");
app.MapOpStreamGrpc();
```

## 📊 Telemetría y Diagnóstico

OpStream expone métricas y trazas nativas compatibles con **OpenTelemetry**, permitiendo monitorizar:
- Operaciones por segundo.
- Conexiones activas.
- Latencia de persistencia.
- Errores de sincronización.

Además, incluye un endpoint de diagnóstico (opcional) para inspeccionar el estado de los documentos en tiempo real.

## 🌟 Ejemplos de Uso

Este framework está diseñado para alimentar una gran variedad de escenarios:
- Editores de texto colaborativos (Monaco, TipTap, CodeMirror).
- Pizarras blancas (Canvas/SVG).
- Dashboards en tiempo real.
- Aplicaciones móviles con sincronización offline-first.
- Bots de automatización que interactúan con documentos compartidos.

Consulte el archivo [Lista de ejemplos.md](./Lista de ejemplos.md) para ver una lista detallada de implementaciones sugeridas.

## 📄 Licencia

Este proyecto se distribuye bajo la licencia MIT. Consulte el archivo `LICENSE` para más detalles.
