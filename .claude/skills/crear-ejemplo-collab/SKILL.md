---
name: crear-ejemplo-collab
description: >
  Convierte una librería/editor web de terceros en un ejemplo colaborativo de OpStream,
  partiendo de un link. Usa este skill cuando el usuario pase la URL de una librería JS o
  de un editor (canvas, diagramas, hoja de cálculo, grafo de nodos, mapa, texto rico, etc.)
  y pida "hazlo colaborativo", "crea un ejemplo/sample de OpStream con esto",
  "collaborabiliza este editor", "monta un demo con esta librería", o similar. También
  aplica para añadir presencia, feedback de edición remota o comentarios anclados a un
  ejemplo existente, y para **hostear un sample (JS o Blazor) en OpStream.DemoHost**
  (carpeta `samples/` → páginas `/samples/*` del demo público). Triggers: link a librería +
  "colaborativo/collab/sample/ejemplo/demo"; o "hostea/monta este sample en DemoHost".
---

# Crear un ejemplo colaborativo de OpStream desde una librería

Proceso completo y probado (2026-05-30) para ir de **"aquí tienes el link de una
librería"** a **un sample HTML+JS funcional en `samples/<nombre>-collab/`**.

Referencias vivas en el repo (cópialas y adapta): `samples/threejs-editor`,
`samples/luckysheet-collab`, `samples/fabric-collab` (el más completo:
presencia + feedback + comentarios), `samples/kanban-collab`,
`samples/litegraph-collab`, `samples/gojs-collab`.

---

## Paso 0 — La regla de los 3 ganchos (¿es viable?)

Un editor se puede "collaborabilizar desde fuera" solo si expone:

1. **Observar ediciones locales** — un evento/callback de cambio (o un
   `history`/`command` que se pueda envolver).
2. **Aplicar ediciones remotas** — una API para mutar su estado por código.
3. **Identidad estable** por elemento editable — un `id`/`key`/coordenada que
   sea el mismo en todos los peers.

**Banderas rojas (no es viable de forma genérica):** el editor es dueño de su
DOM y lo reescribe de forma opaca, no expone API de aplicar, o no hay identidad
estable. Caso real fallido: **Medium** (editor propietario que reconcilia su
propio DOM → guerra de `innerHTML`, corrupción). Si solo tiene 1 y 2 pero no 3
(p.ej. **Drawflow**, ids enteros no preservados al recrear), hace falta mapear
ids propios — más fricción.

**Licencia:** prioriza **MIT/permisiva**. Si es comercial (p.ej. **GoJS**), usa
el build de evaluación (con watermark), funciona idéntico para demo, y deja un
aviso claro de licencia en el README.

---

## Paso 1 — Confirmar la API real (NUNCA de memoria)

Antes de escribir una línea, usa **WebFetch** sobre el README/fuente/docs de la
librería y extrae lo EXACTO:

- Nombres de **eventos de cambio** y sus argumentos.
- Métodos para **crear/mutar/borrar** elementos y **conexiones**.
- **Serialización** (toObject/serialize/export) y si **preserva los ids** al
  recrear (clave para la identidad estable).

Ejemplos verificados: three.js (`editor.history.execute` + `uuid`); Luckysheet
(`cellUpdated` + `setCellValue`); LiteGraph (`onNodeAdded/onConnectionChange`,
`LGraphCanvas.onNodeMoved`, `serialize`/`configure`, `add` preserva `node.id`);
Fabric (`object:added/modified/removed`, `enlivenObjects`, `toObject(['id'])`);
GoJS (`addModelChangedListener` + `e.isTransactionFinished`, `addNodeData`/
`setDataProperty`, claves uuid vía `makeUniqueKeyFunction`).

Documenta en el README cualquier punto que NO pudiste confirmar (lo verifica el
usuario al ejecutar). Nunca inventes firmas.

---

## Paso 2 — Elegir motor y esquema de claves

| Lo que edita el usuario | Motor | Esquema de rutas |
|---|---|---|
| Objetos/elementos por id (canvas, 3D, nodos, tarjetas) | **JSON CRDT** | `things.<id>` (un registro por elemento) |
| Hoja de cálculo / grid | JSON CRDT o Table CRDT | `cells.<fila>_<col>` |
| Texto plano / código | Text OT | un documento de texto (ver MonacoCollaborativeJs) |
| Texto rico | Rich Text | — |
| Árbol / bloques | Tree CRDT | `nodes.<id>` |
| Formulario | Form OT | — |

El **caballo de batalla** es JSON CRDT con **un registro por id**. Para
estructuras con enlaces (grafos), usa `nodes.<key>` + `links.<key>`; si el
callback de cambios es grueso (no dice qué cambió), usa un registro `graph` con
el `serialize()` completo (LWW) para la estructura y ops granulares para
posiciones (ver litegraph/gojs).

---

## Paso 3 — Estructura del sample

```
samples/<nombre>-collab/
  package.json        # vite + @microsoft/signalr
  vite.config.js      # root=sampleDir, port 5173, proxy /collab → server (ws:true)
  index.html          # carga la librería SIN MODIFICAR desde CDN + badge #status
  src/main.js         # instancia el editor + toolbar + crea la CollabSession
  src/collab-session.js  # el patrón reutilizable (ver Paso 4)
  src/style.css
  README.md
```

`vite.config.js` debe proxyear `/collab` al servidor OpStream. Si la librería se
carga desde un sitio externo y necesitas mismo-origen (iframe/contentWindow),
proxea también ese host (ver `threejs-editor`: proxy `/three.js-dev` →
`https://threejs.org`).

`package.json` deps: `@microsoft/signalr` (^8); devDeps: `vite`.

---

## Paso 4 — El patrón CollabSession (copiar y adaptar)

Cliente SignalR JS contra `/collab`. Estructura idéntica en todos los samples;
solo cambia el "glue" de captura/aplicar.

```js
import * as signalR from '@microsoft/signalr';

const PROTOCOL_VERSION = 1, DOCUMENT_TYPE = 'json', PREFIX = 'things.';
const b64ToUtf8 = b64 => new TextDecoder().decode(Uint8Array.from(atob(b64), c => c.charCodeAt(0)));
const utf8ToB64 = s => { const a = new TextEncoder().encode(s); let b=''; for (const x of a) b+=String.fromCharCode(x); return btoa(b); };
const opToPayload = o => utf8ToB64(JSON.stringify(o)); // byte[] sobre SignalR JSON = string base64
```

Flujo:
1. **connect**: `HubConnectionBuilder().withUrl(url).withAutomaticReconnect()`,
   registrar `connection.on('ReceiveOp', (payloadB64, rev) => applyBatch(...))`,
   `start()`, `invoke('JoinDocument', documentId, 'json', 1)` →
   `{ revision, snapshot, pendingOps }`. Sembrar snapshot, aplicar pendingOps,
   instalar hooks del editor.
2. **Capturar** (evento del editor): construir op
   `{ $type: 'set'|'del', path: PREFIX+id, value, timestamp: Date.now(), peerId }`,
   coalescer en un `Map` por `path`, y `_flush()` →
   `invoke('SendOp', documentId, opToPayload(batch), revision)` (actualiza
   `revision = r.newRevision`).
3. **Aplicar** (`ReceiveOp` y snapshot): por cada op, mutar el editor por su API
   (`set` → crear/actualizar; `del` → borrar), **envuelto en un guard
   `remoteApplyDepth`** para que tus propios cambios al aplicar NO se reenvíen.

**Detalles críticos (verificados):**
- El discriminador del op JSON es **`$type`** (NO `type`; las docs están
  desactualizadas).
- Snapshot JSON CRDT: `{ registers: { <path>: { value, isDeleted, timestamp, peerId } } }`.
- Supresión de eco: contador `remoteApplyDepth` (Fabric/LiteGraph/GoJS), o
  match por valor (Luckysheet), o flag `excludeFromExport` para overlays.
- Identidad: si la lib preserva ids al recrear → úsalos; si no → genera tu uuid
  y guárdalo en los datos del elemento.
- El servidor **no** reenvía el op a su emisor, así que todo `ReceiveOp` es
  remoto (útil para atribuir feedback).

---

## Paso 5 — Extras "pro" (del showcase fabric-collab)

**Presencia + feedback de edición remota:**
- Cada peer difunde `invoke('UpdateAwareness', documentId, { peerId, name, color })`.
- `connection.on('ReceiveAwareness', data => ...)`: lee `state.data` (camelCase,
  NO `dataJson`); el payload puede venir como objeto único o array → maneja ambos.
- Como cada op lleva `peerId`, al recibir un op remoto puedes pintar un **borde
  de color del autor + etiqueta con su nombre** sobre el elemento tocado, y
  desvanecerlo a los ~2.5s. El overlay debe ser NO sincronizable.

**Comentarios anclados a un elemento:**
- Hub: `CreateComment(documentId, NewCommentCmd)`, `ListOpenComments(documentId)`,
  `ResolveComment(documentId, commentId)`, `EditComment`, `DeleteComment`.
- `NewCommentCmd = { Body, Anchor, ParentCommentId? }` donde **`Anchor` es
  `CommentAnchor { Kind, Payload }`** (objeto, NO un string `anchorJson`). Ancla
  un elemento con `{ kind: '<libreria>-elemento', payload: { id } }`; usa un
  `Kind` propio si no necesitas rebasing (los ids inmutables no lo necesitan).
- Eventos: `ReceiveCommentCreated/Updated/Deleted`.
- `ListOpenComments` devuelve TODOS los no-borrados (incluidos resueltos), y los
  "resolve" llegan como **`ReceiveCommentUpdated`** → **filtra por `isResolved`**.
- `authorId` es el ConnectionId del servidor (no tu peerId de presencia).
- Pin 💬 anclado al elemento (overlay HTML reposicionado al re-render) + panel
  lateral para crear/resolver.

---

## Paso 6 — Contrato del servidor (qué tiene que estar vivo)

Host OpStream con transporte **SignalR** en `/collab` + el **engine** elegido
habilitado (json/text/...) + subsistema de **comments** (el host por defecto lo
trae). Arranque rápido: `docker run -p 8080:8080 opstreamcollab/opstream`
(puerto 8080; en dev local el servidor puede estar en `:50109` → ajusta el proxy
de vite). WS alternativo en `/collab-ws`.

Métodos hub: `JoinDocument`, `SendOp`, `UpdateAwareness`, `CreateComment`,
`ListOpenComments`, `ResolveComment`, `EditComment`, `DeleteComment`.
Eventos: `ReceiveOp`, `ReceiveAwareness`, `PeerDisconnected`,
`ReceiveCommentCreated/Updated/Deleted`.

---

## Paso 7 — Verificar y documentar

- **Sintaxis**: `node --check vite.config.js`, `node --input-type=module --check < src/*.js`, `JSON.parse(package.json)`.
- **NO puedes ejecutar la colaboración de dos navegadores tú mismo.** El listón
  es: sintaxis OK + API contrastada con docs. **Documenta en el README los
  puntos no verificados** y los fallos típicos a revisar:
  - callback vs promise (Fabric v5 callback / v6 promise en `enlivenObjects`).
  - nombres exactos de eventos/métodos según la versión.
  - forma del payload de `ReceiveAwareness` (único vs lista).
  - forma de `NewCommentCmd`/`CommentAnchor` y casing de los DTO.
- **README**: qué es (resultado) · cómo funciona (engine + esquema de claves +
  captura/aplicar) · cómo ejecutar (`docker run` + `npm install && npm run dev`
  + dos pestañas) · limitaciones · nota de licencia.

## Paso 8 — Integrar en las docs (opcional)
Si encaja, añade un callout "Showcase" en `mkdocs/docs/index.md` y/o una recipe
en `mkdocs/docs/recipes/` enlazando al sample en GitHub. Rebuild con
`mkdocs build --strict`.

---

## Paso 9 — Hostear el sample en OpStream.DemoHost (demo público)

`OpStream.DemoHost` es un **repo aparte** (`OpStreamCollab/OpStream.DemoHost`)
que consume OpStream como **paquetes NuGet** y sirve los samples del repo OpStream
en `https://hostdemo.opstream.stream/samples/<slug>/`. Catálogo único:
`OpStream.DemoHost/samples.manifest.json` (lo usan CI, la galería in-app y los
links de los docs). Memoria viva: `demohost-samples-hosting`.

### 9a. Samples JS (Vite / estático) — los fáciles
- Todos usan **`/collab` relativo** ⇒ servidos mismo-origen desde DemoHost
  funcionan **sin tocar URLs**.
- Build con base path: `npm run build -- --base=/samples/<slug>/` → copiar `dist/`
  a `wwwroot/samples/<slug>/`. Estático plano (Monaco): copiar tal cual.
- **threejs**: además vendoriza el editor (`git clone --depth 1 --branch r170
  mrdoob/three.js`, copia `editor/build/files/examples/jsm`) bajo
  `wwwroot/samples/threejs/three.js-dev/`, y el **iframe src debe ser relativo**
  (mismo-origen: `collab-session` entra al `contentWindow`).
- `npm ci` (lo usa el CI) **exige `package-lock.json` commiteado** — si el sample
  no lo tiene, commitéalo.

### 9b. Samples Blazor — patrón RCL (Razor Class Library)
DemoHost ES Blazor, así que los samples Blazor se integran como **páginas**, no
como estáticos. Molde de referencia ya en el repo: `samples/OpStream.CollabHtmlEditor`.

1. Extrae la UI reutilizable a un RCL `samples/<Nombre>.View/` (`Sdk.Razor`,
   conserva `RootNamespace` para no tocar namespaces), con un componente raíz
   **sin `@page`** (p.ej. `FormEditorDemo.razor`). Mueve componentes con `git mv`.
2. El host standalone se queda con un `Home.razor` fino (`@page "/"` +
   `<XxxDemo/>`) y referencia el RCL por `ProjectReference`.
3. **Refs OpStream → paquetes**, NO project refs a `src/`. Pínalas a la
   **prerelease que trae la feature** (hoy `1.0.0-ci.11`): `*-*` resuelve al
   **estable `1.0.0`** que en semver gana a la prerelease y **carece de la API de
   comentarios** (`CommentDto`/`OnCommentCreated`). `OpStreamJsonOptions` vive en
   el paquete `OpStream.Constants` (añádelo si lo usa el componente).
4. RCL+host en **net9** está bien aunque DemoHost sea **net10** (referencia
   cross-major OK). `Microsoft.AspNetCore.Components.Web` 9.0.x para net9.
5. En DemoHost:
   - csproj: `ProjectReference Include="$(SamplesSrcDir)\<Nombre>.View\..."`
     (propiedad overridable por CI; default `..\..\OpStream\samples`).
   - `Program.cs`: registra el **cliente** OpStream una vez:
     `AddOpStreamClient().UseSignalRTransport(o => o.HubUrl =
     config["OpStream:HubUrl"] ?? "https://hostdemo.opstream.stream/collab")`
     (dev: `http://localhost:5555/collab` en appsettings.Development.json).
   - Servicios por librería: Blazorise (`AddBlazorise().AddBootstrapProviders()
     .AddFontAwesomeIcons(); AddBlazoriseRichTextEdit()`), Radzen
     (`AddRadzenComponents()`).
   - Página wrapper `Components/Pages/Samples/<X>Sample.razor` con
     `@page "/samples/<slug>"` que renderiza el componente del RCL.
6. **CSS/JS host-agnóstico** (sin tocar cada App.razor):
   - CSS de la librería (Blazorise/Bootstrap/Radzen theme) → `<HeadContent>` en el
     componente demo (scoped a la página, no reestiliza todo el host).
   - Adaptadores JS del sample → se envían en el `wwwroot/` del RCL y se cargan
     desde el propio componente con `await JS.InvokeAsync<IJSObjectReference>(
     "import", "./_content/<Nombre>.View/<adapter>.js")` (un `<script>` renderizado
     por Blazor NO se ejecuta). Excepción: Radzen.Blazor.js + radzen-collab-adapter
     se cargan global en el App.razor de DemoHost.
7. **Radzen**: su sample standalone es un repo git **anidado, no trackeado** por
   OpStream → no lo metas en el CI. Hostéalo SIN RCL nuevo: referencia el RCL
   trackeado `OpStream.CollabHtmlEditor` + paquete `Radzen.Blazor` y compón
   `<CollabHtmlEditor><RadzenHtmlEditor/></CollabHtmlEditor>` en la página. Omite
   `UploadUrl` (no exponer subida de ficheros al webroot del demo público).

### 9c. CI / Docker (en el repo DemoHost)
- `scripts/prepare-samples.sh <opstream-repo>` = **fuente única** de la copia
  (build Vite, vendor threejs, copia RCLs a `_samples_src/`, estáticos a
  `wwwroot/samples/`). Lo llama `build-image.yml` (checkout de OpStream + Node) y
  se puede correr en local.
- **Dockerfile**: copia `_samples_src/` **antes** del `dotnet restore` (los
  ProjectReference deben existir) y pasa `-p:OpStreamVersion` + `-p:SamplesSrcDir=_samples_src`.
- **GOTCHA crítico**: `_samples_src` queda DENTRO del proyecto ⇒ el Razor/Web SDK
  auto-globa sus `.razor`/`.cs` y los compila además del RCL → **doble compilación
  (RZ10009)**. Fix: `<DefaultItemExcludes>$(DefaultItemExcludes);_samples_src\**</DefaultItemExcludes>`.
  (No salta en build local porque ahí `_samples_src` no existe.)
- **Servir estáticos**: `UseStaticFiles`+`UseDefaultFiles` (PhysicalFileProvider,
  `RequestPath="/samples"`) en Program.cs — middleware antes que endpoints, gana
  para ficheros físicos; las rutas Blazor `/samples/{...}` no chocan (no hay
  fichero físico → pasan al router).
- `wwwroot/samples/` y `_samples_src/` van a **`.gitignore`** (regenerados por CI).
- ci.11 movió tipos de storage (`DocumentSnapshot`/`StoredOp`/`HistoryMilestone`)
  de `OpStream.Server.Models` → **`OpStream.Shared.Messages`** (ajusta usings del
  código propio de DemoHost si subes de versión).

### 9d. Galería + docs
- Galería in-app `/samples`: página Blazor que lee `samples.manifest.json` (vía un
  servicio singleton) y pinta cards con Live demo + Source.
- Docs mkdocs: página `docs/samples.md` en el nav + admonición "Try it live"
  (`▶ Live demo` + `</> Source`) al inicio de cada recipe. Valida con
  `mkdocs build --strict`. Para Radzen el "source" apunta al RCL trackeado
  (`OpStream.CollabHtmlEditor`), no al repo anidado (evita un link 404).

---

## Errores ya cometidos (no repetir)
- **No reescribir un fichero basándose en una lectura sospechosa/corrupta del
  tooling** (sobrescribí un `vite.config.js` bueno). Si una lectura sale rara,
  re-léela o confía en `node --check`, no escribas a ciegas.
- **No inventar firmas de API** — siempre WebFetch primero.
- Confundir `type` con `$type`, `anchorJson` con `Anchor{Kind,Payload}`, o tratar
  los "resolve" como deletes.
