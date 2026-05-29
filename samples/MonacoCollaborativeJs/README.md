# Collaborative Monaco Editor — plain HTML + JavaScript (no Blazor)

A self-contained sample that turns a standard [Monaco Editor](https://microsoft.github.io/monaco-editor/)
into a real-time collaborative editor backed by **OpStream**, using only HTML + vanilla
JavaScript over the **WebSocket transport**. No Blazor, no framework, no .NET on the client.

Design rationale and the full protocol write-up: [`design/monaco-collab-html-js.md`](../../design/monaco-collab-html-js.md).

## Run

```bash
dotnet run --project samples/MonacoCollaborativeJs
```

Then open the printed URL (default <http://localhost:5179>) in **two browser tabs**
and type — edits converge live.

- Choose a document / name with query params: `?doc=my-doc&name=Ada`.
- Storage is in-memory and single-node by default (see `Program.cs` to switch to
  Postgres/Redis/etc. and a Redis backplane for multi-node).

## How it works

The host (`Program.cs`) is ~15 lines: it wires OpStream with the built-in `text`
engine (`TextOtEngine`), serves `wwwroot`, and maps the WebSocket endpoint at
`/collab-ws`. Everything collaborative lives in three browser modules:

| File | Role |
|---|---|
| `wwwroot/js/ot-text.js` | A faithful JS port of the server's `TextOp` engine: `apply`, `transform`, `compose`, plus `fromMonacoChanges`. Must stay byte-compatible with `src/OpStream.Server/Engine/Text`. |
| `wwwroot/js/opstream-ws.js` | WebSocket envelope: JSON `WebSocketMessage` framing, base64 ↔ bytes, correlation-id request/response, auto-reconnect. |
| `wwwroot/js/monaco-collab.js` | The glue: `attachCollab(editor, opts)` — encodes Monaco changes to ops, runs the client-OT state machine (inflight + buffer), applies remote ops, preserves the caret, and renders remote cursors. |

### Wire facts the JS relies on (verified against the server)

- Frames are UTF-8 JSON of `WebSocketMessage`, **camelCase**; `messageType` is an
  **integer** enum (`JoinRequest=0 … ErrorResponse=8`).
- `byte[]` fields (`snapshot`, `payload`) are **base64**; decoded they are UTF-8 JSON
  of the engine state (`{"content":"…"}`) / op (`{"components":[…]}`).
- The server **excludes the sender** from op broadcasts, so no self-echo suppression
  is needed.
- Server tie-break is `Transform(incoming, existing, ExistingWins)`; the client uses
  symmetric priorities so committed remote ops win insert-at-same-offset ties.

## Public API

```js
import { attachCollab } from "./js/monaco-collab.js";

const editor = monaco.editor.create(el, { language: "plaintext" });
const session = attachCollab(editor, {
  documentId: "monaco-doc-1",
  presence: { name: "Ada", color: "#e91e63" }, // optional remote-cursor presence
  getAuthToken: () => localStorage.token,       // optional; appended as ?access_token=
  onStatus: (s) => console.log(s),
});
// session.dispose() to tear down.
```
