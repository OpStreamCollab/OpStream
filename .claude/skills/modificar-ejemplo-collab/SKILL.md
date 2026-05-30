---
name: modificar-ejemplo-collab
description: >
  Cómo MODIFICAR un ejemplo/sample de OpStream existente y propagar el cambio al
  demo público (OpStream.DemoHost). Usa este skill cuando el usuario pida cambiar,
  arreglar, mejorar o añadir algo a un sample ya creado ("modifica el ejemplo de
  gojs", "arregla X en el sample de fabric", "añade un botón a luckysheet",
  "cámbiale el color al canvas", "el sample Y falla, corrígelo"), o pregunte dónde
  se editan los samples / cómo se sincronizan con DemoHost / por qué su cambio no
  aparece en el demo. Para CREAR un sample nuevo desde una librería usa
  `crear-ejemplo-collab`.
---

# Modificar un ejemplo de OpStream y sincronizarlo con el demo

Dos repos, hermanos en disco:

- **`C:\OpStream`** — repo `OpStreamCollab/OpStream`. Aquí viven **los samples**
  (`samples/<nombre>/`). **ES LA ÚNICA FUENTE DE VERDAD. Edita SIEMPRE aquí.**
- **`C:\OpStream.DemoHost`** — repo `OpStreamCollab/OpStream.DemoHost`. Hostea los
  samples en `https://hostdemo.opstream.stream/samples/<slug>/`. Consume OpStream
  como paquetes NuGet. Ver memoria [[demohost-samples-hosting]].

> ⛔ **NUNCA edites** `OpStream.DemoHost/wwwroot/samples/**` ni
> `OpStream.DemoHost/_samples_src/**`: son **artefactos generados** (gitignored) que
> el CI/script regeneran desde el repo OpStream. Cualquier cambio ahí se pierde.

---

## Paso 1 — Localiza el sample y su slug

El nombre de carpeta ≠ slug de la URL. El mapeo canónico está en
**`OpStream.DemoHost/samples.manifest.json`**:

| Carpeta (`samples/`) | slug / URL | tipo |
|---|---|---|
| `luckysheet-collab` | `/samples/luckysheet/` | vite |
| `fabric-collab` | `/samples/fabric/` | vite |
| `gojs-collab` | `/samples/gojs/` | vite |
| `kanban-collab` | `/samples/kanban/` | vite |
| `litegraph-collab` | `/samples/litegraph/` | vite |
| `threejs-editor` | `/samples/threejs/` | vite + vendor |
| `MonacoCollaborativeJs` | `/samples/monaco/` | static |
| `BlazorFormEditor` (RCL `BlazorFormEditor.View`) | `/samples/form` | blazor |
| `BlazorTextEditor` (RCL `*.View`) | `/samples/text` | blazor |
| `BlazoriseRichTextEditor` (RCL `*.View`) | `/samples/rich-text` | blazor |
| Radzen → RCL `OpStream.CollabHtmlEditor` | `/samples/html-editor` | blazor |
| `SyncfusionKanban` (RCL `*.View`) | `/samples/syncfusion-kanban` | blazor |

---

## Paso 2 — Edita en `C:\OpStream\samples\<carpeta>\`

**Samples JS/Vite/estáticos** (`src/*.js`, `index.html`, `src/style.css`):
- La lógica colaborativa vive en `src/collab-session.js`; la UI/instanciación en
  `src/main.js`. La librería se carga SIN MODIFICAR desde CDN en `index.html`.
- Si tocas presencia/feedback/comentarios → sigue el **contrato verificado del
  Paso 5 de `crear-ejemplo-collab`** (awareness = `ReceiveAwarenessUpdate`;
  comentario = `anchor:{kind,data}`/`authorPeerId`/`resolvedAt`). NO copies el
  código viejo de fabric-collab (estaba roto).

**Samples Blazor**: edita el componente del **RCL** `samples/<Nombre>.View/`
(no el host fino). Mantén `RootNamespace` y el patrón sin `@page`.

**Comprueba sintaxis siempre** antes de construir:
```bash
node --check vite.config.js
node --input-type=module --check < src/main.js
node --input-type=module --check < src/collab-session.js
```

---

## Paso 3 — Propaga el cambio a DemoHost

### En CI / producción (lo normal)
No hay que hacer nada manual: `OpStream.DemoHost/scripts/prepare-samples.sh`
reconstruye/copia TODO desde el checkout de OpStream antes del build de Docker.
**Basta con commitear el cambio en el repo OpStream**; al disparar el pipeline de
DemoHost se regenera. Ese script es la **fuente única** de la lógica de copia.

### En LOCAL (para probar tu cambio ya)
DemoHost sirve los estáticos desde su `wwwroot/samples/<slug>/`, que NO se
regenera solo. Tras editar un sample **Vite**, reconstrúyelo y cópialo:
```bash
cd C:/OpStream/samples/<carpeta>
MSYS_NO_PATHCONV=1 npx vite build --base="/samples/<slug>/"   # MSYS_NO_PATHCONV en Git Bash
W="C:/OpStream.DemoHost/OpStream.DemoHost/wwwroot/samples/<slug>"
rm -rf "$W"; mkdir -p "$W"; cp -r dist/. "$W/"
```
- **threejs**: al recopiar NO borres `wwwroot/samples/threejs/three.js-dev/` (el
  editor vendorizado). Copia el `dist/` ENCIMA sin `rm -rf` del subdir, o re-vendóralo.
- **Monaco / estáticos**: copia los ficheros tal cual (sin build).
- **Samples Blazor**: NO se copian a wwwroot; basta `dotnet build` de DemoHost
  (referencia el RCL vía `$(SamplesSrcDir)` → recoge tus `.razor`). No hace falta
  reiniciar si solo cambian estáticos servidos; sí recompilar si cambian `.razor`/`.cs`.

> El sample standalone (`npm run dev`, puerto 5173) usa un proxy `/collab` que por
> defecto apunta a `http://localhost:50109`. El server real en local suele ser
> **DemoHost en `:5555`** → o pruebas en `http://localhost:5555/samples/<slug>/`
> (mismo origen, sin proxy), o apuntas el proxy de `vite.config.js` a `:5555`.

---

## Paso 4 — Verifica

- Reconstruido + recopiado → abre `http://localhost:5555/samples/<slug>/`
  (Ctrl+F5 para saltar caché; el hash del bundle cambia en cada build).
- Colaboración: dos pestañas. **No puedes validar tú la colaboración de dos
  navegadores de forma fiable** — para inspeccionar estado usa `window.__collab`
  (la sesión) y `window.__collab.diagram`/`canvas`/etc. en la consola.
- README del sample: actualízalo si el cambio altera comportamiento o contrato.

---

## Paso 5 — Commitea (cuando el usuario lo pida)

- Cambios del sample → **repo `C:\OpStream`** (rama de feature; no commitees en la
  default). Incluye `src/`, `index.html`, `README.md`, y `package-lock.json` si
  `npm install` lo generó (el CI usa `npm ci` y lo necesita).
- **Excluye SIEMPRE** del commit: `samples/*/dist/`, `node_modules/`, y en DemoHost
  `wwwroot/samples/` + `_samples_src/` (todo gitignored/artefactos).
- Si el cambio afecta a cómo DemoHost monta el sample (nuevo slug, nuevo servicio,
  nueva ref RCL) → eso va en el **repo `C:\OpStream.DemoHost`** (Program.cs, csproj,
  `samples.manifest.json`, página `Components/Pages/Samples/*`).
- Mantén los dos repos **consistentes**: si añades un sample/feature que DemoHost
  referencia, commitéalo en AMBOS (un branch por repo).

---

## Gotchas (vividos)
- **Windows**: `npm ci` puede dar `EPERM unlink ...rollup...node` (lock de antivirus
  sobre el binario nativo). Workaround: `rm -rf node_modules && npm install`, o si ya
  hay `node_modules` salta el install y construye directo. `MSYS_NO_PATHCONV=1` para
  que Git Bash no destroce `--base=/samples/...`.
- **El cambio no aparece**: casi siempre es que editaste/probaste contra el artefacto
  (`wwwroot/samples` o el preview en `:5173` sin proxy correcto) en vez de reconstruir
  y recopiar, o caché del navegador (Ctrl+F5).
- **GoJS**: `Diagram.commit(fn)` pasa el Diagram; para mutar datos usa
  `diagram.model.commit(fn)` (si no, `addNodeData is not a function`).
- **No reescribas un fichero sobre una lectura sospechosa del tooling** — re-léelo o
  confía en `node --check`.
