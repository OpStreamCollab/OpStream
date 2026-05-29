# Technical Design Proposal — Collaborative Monaco Editor (plain HTML + JavaScript, no Blazor)

- **Status:** Draft / for discussion
- **Author:** (proposal)
- **Date:** 2026-05-29
- **Scope:** A browser-side integration that turns a standard [Monaco Editor](https://microsoft.github.io/monaco-editor/) instance into a real-time collaborative editor backed by OpStream, using **only HTML + vanilla JavaScript** over the **WebSocket transport**. No Blazor, no .NET on the client, no framework.

---

## 1. Motivation & context

OpStream already drives a collaborative rich-text editor through a Blazor component (`CollabHtmlEditor`) plus a JS adapter (`radzen-collab-adapter.js`) that bridges a **contenteditable** DOM region to the server. That path has two properties we want to drop for this use case:

1. It depends on **Blazor Server** (a .NET circuit, `IJSRuntime`, `DotNetObjectReference`) to mediate between JS and the OpStream client.
2. It treats the document as **opaque HTML** and ships *whole-document replace* operations diffed at the DOM level.

Monaco is different and better suited to true OT:

- It is a **plain-text** editor with a first-class **text model** (`ITextModel`) and a precise change event (`onDidChangeModelContent`) that already tells us *exactly* what changed as `(offset, length, insertedText)` tuples.
- Its content maps **1:1** onto OpStream's `TextOtEngine` / `TextOp` (retain / insert / delete over a `string`). No DOM diffing, no HTML normalization.

So this design connects Monaco **directly** to OpStream's WebSocket endpoint from the browser, speaking the existing wire protocol in JSON, and performing **client-side operational transformation** so local typing and remote edits converge.

### Goals

1. Pure browser integration: an HTML page + a few JS modules + the Monaco CDN/npm bundle. The server is unchanged.
2. Use the existing **WebSocket transport** (`/ws` endpoint, `WebSocketMessage` envelope) and the existing **`TextOtEngine`** (`documentType = "text"`).
3. Correct convergence under concurrent edits via standard **client OT** (in-flight op + buffer, transform against server echoes).
4. Cursor/selection preservation and (optionally) **presence/awareness** of remote cursors.
5. Snapshot seeding, reconnection/resync, and clean teardown.

### Non-goals

- Rich text / formatting (bold, tables, images). That is the `RichTextEngine` path; Monaco here is plain text/code. (A follow-up could target `RichTextEngine` for a rich Monaco-like editor, but Monaco's value is code/plaintext.)
- Server changes. This rides entirely on the current WebSocket + `TextOtEngine` surface.
- A build toolchain requirement. The design works with plain `<script type="module">`; bundling is optional.

---

## 2. Background — the primitives we build on (verified against the code)

### 2.1 WebSocket wire protocol

`src/OpStream.Server.Transports.WebSockets/WebSocketTransport.cs` accepts a WebSocket, reads UTF-8 **JSON text frames**, and deserializes them into `WebSocketMessage` with **camelCase** property naming (`PropertyNamingPolicy = CamelCase`). The envelope (`src/OpStream.Shared.Messages/WebSocketMessages.cs`):

```csharp
class WebSocketMessage {
  string?               CorrelationId;
  WebSocketOpMessageType MessageType;   // serialized as a number by default (enum)
  JoinRequestData?      JoinRequest;
  JoinResponseData?     JoinResponse;
  OpRequestData?        OpRequest;
  OpResponseData?       OpResponse;
  AwarenessRequestData? AwarenessRequest;
  ReceiveOpEventData?   ReceiveOpEvent;
  ReceiveAwarenessEventData? ReceiveAwarenessEvent;
  PeerDisconnectedEventData? PeerDisconnectedEvent;
  string?               ErrorMessage;
  // … comment payloads (out of scope here) …
}
```

Message types the server handles **inbound** (`HandleMessage` switch): `JoinRequest`, `OpRequest`, `AwarenessRequest`. It emits **outbound**: `JoinResponse`, `OpResponse`, `ReceiveOpEvent`, `ReceiveAwarenessEvent`, `PeerDisconnectedEvent`, `ErrorResponse`.

Payload records:

```csharp
record JoinRequestData(string DocumentId, string DocumentType, int ClientProtoVersion);
record JoinResponseData(long Revision, byte[] Snapshot, IEnumerable<AwarenessState> Awareness);
record OpRequestData(string DocumentId, byte[] Payload, long BaseRevision);
record OpResponseData(bool Success, long NewRevision, string? ErrorMessage);
record AwarenessRequestData(string DocumentId, string DataJson);
record ReceiveOpEventData(byte[] Payload, long NewRevision);
record ReceiveAwarenessEventData(IEnumerable<AwarenessState> Awareness);
record PeerDisconnectedEventData(string PeerId);
```

**Two wire facts that matter for JS:**

- `WebSocketOpMessageType` is a C# enum; with the default serializer it goes on the wire as an **integer** (`JoinRequest = 0`, `JoinResponse = 1`, `OpRequest = 2`, `OpResponse = 3`, `AwarenessRequest = 4`, `ReceiveOpEvent = 5`, `ReceiveAwarenessEvent = 6`, `PeerDisconnectedEvent = 7`, `ErrorResponse = 8`). The JS client must use these numeric values (or we add a `JsonStringEnumConverter` server-side — see §9.1).
- `byte[]` fields (`Snapshot`, `Payload`) are serialized by `System.Text.Json` as **base64 strings**. JS must base64-decode them. The decoded bytes are **UTF-8 JSON** of the engine's op/state.

### 2.2 The op model (`TextOtEngine` / `TextOp`)

`src/OpStream.Server/Engine/Text/TextOp.cs`:

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Retain), "retain")]
[JsonDerivedType(typeof(Insert), "insert")]
[JsonDerivedType(typeof(Delete), "delete")]
abstract record TextOpComponent;
record Retain(int Count)  : TextOpComponent;
record Insert(string Text): TextOpComponent;
record Delete(int Count)  : TextOpComponent;

record TextOp(IReadOnlyList<TextOpComponent> Components);
record TextDocument(string Content);   // the engine state
```

So an `OpRequest.Payload`, once base64-decoded and UTF-8-parsed, is JSON like:

```json
{ "components": [ { "type": "retain", "count": 12 },
                  { "type": "insert", "text": "hello" },
                  { "type": "delete", "count": 3 } ] }
```

And the `JoinResponse.Snapshot`, decoded, is the engine state for `documentType = "text"`:

```json
{ "content": "the full current document text" }
```

> **Casing note.** The engine op JSON uses the OpStream server-side serializer (`OpStreamJsonOptions`). `[JsonPropertyName]`/casing must match what the server expects — the discriminator is literally `"type"` with values `retain|insert|delete`, and component fields are `count` / `text`. The JS encoder must emit exactly this shape. (Confirm `Count`/`Text` casing against `OpStreamJsonOptions`; the existing `radzen-collab-adapter.js._transformCursor` already reads both `count|Count` and `text|Text`, implying camelCase on the wire.)

### 2.3 Semantics of `TextOp` (from `TextOtEngine.Apply`)

- Components are applied left→right with a cursor `index` into the current content.
- `Retain(n)` copies `n` chars; `Insert(t)` appends `t`; `Delete(n)` skips `n` chars.
- **Any tail not covered by the op is implicitly retained** (`Apply` appends the remainder). So a single keystroke at offset `k` in a doc of length `L` is `[Retain(k), Insert("x")]` — you do **not** need a trailing `Retain(L-k)`. This keeps ops tiny.
- Lengths are **UTF-16 code units** (.NET `string.Length`), which is exactly what Monaco uses for offsets (`model.getOffsetAt`/`getPositionAt`). They line up — but **surrogate pairs** (emoji) count as 2 units on both sides, so no conversion is needed as long as we never split a pair (Monaco's change ranges never do).

### 2.4 Server-side flow (what we get for free)

`router.JoinDocumentAsync` → returns `{ Revision, Snapshot, CurrentAwareness }`. `router.ApplyOpAsync(peerId, docId, payload, baseRevision)` → the server **transforms** the incoming op against everything committed since `baseRevision`, applies it, persists it, returns `{ Success, NewRevision }`, and **broadcasts** a `ReceiveOpEvent(payload, newRevision)` to the session (including, per the existing adapter's `_ownRevisions` self-echo handling, the author — so we must suppress our own echo). `UpdateAwarenessAsync` fans out `ReceiveAwarenessEvent`.

---

## 3. Architecture overview

```
 ┌──────────────────────────── Browser (HTML + JS, no framework) ────────────────────────────┐
 │                                                                                            │
 │   index.html                                                                               │
 │     └── <div id="editor">                                                                  │
 │                                                                                            │
 │   monaco (CDN/npm)  ──creates──►  ITextModel  ──onDidChangeModelContent──┐                 │
 │                                        ▲                                  │                 │
 │                                        │ applyEdits (guarded)            ▼                 │
 │                                 ┌──────┴───────────────────────────────────────┐          │
 │                                 │        monaco-collab.js  (the adapter)        │          │
 │                                 │  • change→TextOp encoding                     │          │
 │                                 │  • client OT: inflight + buffer               │◄────┐    │
 │                                 │  • remote op → monaco edits                   │     │    │
 │                                 │  • cursor/awareness                           │     │    │
 │                                 └──────┬───────────────────────────────┬───────┘     │    │
 │                                        │ uses                          │ uses        │    │
 │                                 ┌──────▼────────┐               ┌───────▼─────────┐   │    │
 │                                 │  ot-text.js   │               │ opstream-ws.js  │   │    │
 │                                 │ transform/    │               │ WebSocket conn, │   │    │
 │                                 │ compose/apply │               │ JSON envelope,  │   │    │
 │                                 │ (TextOp in JS)│               │ base64, corr-id │   │    │
 │                                 └───────────────┘               └────────┬────────┘   │    │
 └─────────────────────────────────────────────────────────────────────────┼───────────┘    │
                                                                             │ ws://…/ws       │
                                                                    ┌────────▼─────────────────▼──┐
                                                                    │  OpStream WebSocketTransport │
                                                                    │  DocumentRouter + TextOtEngine│
                                                                    └──────────────────────────────┘
```

Three small JS modules, no framework:

| Module | Responsibility |
|---|---|
| `opstream-ws.js` | WebSocket lifecycle: connect, JSON encode/decode of `WebSocketMessage`, base64 ↔ bytes, correlation-id request/response, event dispatch, reconnect. |
| `ot-text.js` | A faithful JS port of `TextOp`: `apply(text, op)`, `transform(a, b, priority)`, `compose(a, b)`, plus `fromMonacoChange(...)`. Mirrors `TextOtEngine` semantics so the client can transform its own un-acked edits. |
| `monaco-collab.js` | The glue: wires a Monaco instance to a document id, encodes local changes, runs the client-OT state machine, applies remote ops, and manages cursors/awareness. |

---

## 4. Mapping Monaco edits → `TextOp`

Monaco's `onDidChangeModelContent` fires an `IModelContentChangedEvent`:

```ts
interface IModelContentChange {
  range: IRange; rangeOffset: number; rangeLength: number; text: string;
}
e.changes: IModelContentChange[]   // multiple simultaneous edits (multi-cursor, find&replace)
```

Each change means: *at absolute offset `rangeOffset`, `rangeLength` chars were removed and `text` was inserted.* Monaco delivers multiple changes **sorted by descending offset** (last-in-document first), so applying them in array order against the *original* text is consistent.

### 4.1 Encoding algorithm

Convert one event into **one** `TextOp` over the pre-edit document:

1. Sort `e.changes` **ascending** by `rangeOffset` (reverse Monaco's order) so we sweep the document left→right.
2. Walk a cursor `pos = 0`. For each change `c`:
   - `Retain(c.rangeOffset - pos)` if positive (skip untouched text).
   - `Delete(c.rangeLength)` if `> 0`.
   - `Insert(c.text)` if non-empty.
   - `pos = c.rangeOffset + c.rangeLength`.
3. No trailing retain needed (§2.3 — the engine implicitly retains the tail).
4. Compact adjacent same-type components (the engine also compacts, but small ops keep traffic down).

```js
// ot-text.js
export function fromMonacoChanges(changes) {
  const sorted = [...changes].sort((a, b) => a.rangeOffset - b.rangeOffset);
  const comps = [];
  let pos = 0;
  for (const c of sorted) {
    if (c.rangeOffset > pos) comps.push({ type: "retain", count: c.rangeOffset - pos });
    if (c.rangeLength > 0)   comps.push({ type: "delete", count: c.rangeLength });
    if (c.text.length > 0)   comps.push({ type: "insert", text: c.text });
    pos = c.rangeOffset + c.rangeLength;
  }
  return compact({ components: comps });
}
```

### 4.2 EOL hazard

Monaco models have an EOL setting (`\n` or `\r\n`). Offsets and `text` are reported in the **model's** EOL. To avoid the client and server disagreeing on string length, **pin the model EOL to `\n`** at creation (`model.setEOL(monaco.editor.EndOfLineSequence.LF)`) and ensure the seeded snapshot uses `\n`. Paste of CRLF content is normalized by Monaco to the model EOL, so a single setting keeps both sides consistent.

---

## 5. Applying remote ops → Monaco

A `ReceiveOpEvent` carries a base64 `Payload` (a `TextOp` JSON) and `NewRevision`. To apply it without bouncing it back as a local change:

1. Base64-decode → UTF-8 → `JSON.parse` → `op`.
2. **Echo suppression:** if `NewRevision` is one we authored (tracked in a `Set` of our acknowledged revisions), drop it.
3. **Client-OT transform** (see §6): transform the incoming op against our `inflight` and `buffer` ops so it applies cleanly on *our* current text; symmetrically transform `inflight`/`buffer` against it so future acks line up.
4. Convert the (transformed) `TextOp` into Monaco edits and apply with a **guard flag** so our own `onDidChangeModelContent` handler ignores them:

```js
function applyRemote(model, op) {
  const edits = [];
  let offset = 0;
  for (const c of op.components) {
    if (c.type === "retain") offset += c.count;
    else if (c.type === "delete") {
      const start = model.getPositionAt(offset);
      const end   = model.getPositionAt(offset + c.count);
      edits.push({ range: monaco.Range.fromPositions(start, end), text: "" });
      offset += c.count;            // consume from the (pre-edit) doc
    } else if (c.type === "insert") {
      const p = model.getPositionAt(offset);
      edits.push({ range: monaco.Range.fromPositions(p, p), text: c.text });
      // insert does NOT advance the source offset
    }
  }
  applyingRemote = true;
  try { model.applyEdits(edits); }   // does not pollute the user's undo stack
  finally { applyingRemote = false; }
}
```

> **Offset bookkeeping caveat.** `applyEdits` takes ranges in the **current** coordinate space and applies them atomically, but multiple edits in one batch shift each other. The robust approach is to compute *all* ranges from the pre-edit model (as above, since `getPositionAt` is called before `applyEdits`) and pass them in **descending start-offset order** so earlier edits don't invalidate later offsets — or apply them one-by-one back-to-front. The adapter sorts the produced `edits` by descending start before calling `applyEdits`.

Cursor preservation: Monaco keeps the local selection across `applyEdits` reasonably well, but for inserts/deletes *before* the caret we adjust using the same retain/insert/delete sweep the existing adapter's `_transformCursor` uses (§7).

---

## 6. Concurrency: the client-OT state machine

The server is authoritative and transforms every op against concurrent history, but the **client** must also transform locally because it can type while an op is un-acknowledged. We use the classic three-slot model (à la ot.js / the standard client OT FSM):

- `revision` — last server revision we've synchronized to.
- `inflight` — the single op we've sent and are awaiting `OpResponse` for (or `null`).
- `buffer` — local edits accumulated *while* `inflight` is outstanding, composed into one op (or `null`).

**Transitions:**

| State | Event | Action |
|---|---|---|
| synced (`inflight==null`) | local change | send it as `inflight`, `baseRevision = revision` |
| awaiting (`inflight!=null`, `buffer==null`) | local change | `buffer = op` |
| awaiting, `buffer!=null` | local change | `buffer = compose(buffer, op)` |
| awaiting | `OpResponse{ok,newRev}` | `revision = newRev`; record newRev as ours; if `buffer`: send `buffer` as new `inflight` (baseRevision = newRev), `buffer = null`; else `inflight = null` |
| any | `ReceiveOpEvent(remote, newRev)` (not ours) | `remote' = transform(remote against inflight then buffer)`; `inflight = transform(inflight against remote)`; `buffer = transform(buffer against remote')`; `apply remote'' to Monaco`; `revision = newRev` |

`transform(a, b, priority)` and `compose(a, b)` are the JS ports of `TextOtEngine.Transform`/`Compose`. **Priority must match the server's tie-break.** The server, when *it* transforms our op, uses a fixed priority (see `DocumentRouter`/engine call site — typically `ExistingWins` for already-committed ops). The client uses the symmetric choice so both sides converge: when transforming a **remote already-committed** op against our **local pending** op, the remote is "existing" → our local edit yields on insert-at-same-position ties. Document this once and unit-test convergence (§11).

> **Simpler fallback (optional).** If full client OT is deemed too heavy for a first cut, fall back to the **coalescing** model the existing `radzen-collab-adapter.js` uses: at most one op in flight, newer local edits overwrite the pending snapshot, and rely on the server transform + a re-seed on divergence. This is simpler but can momentarily fight the user's caret under heavy concurrency. **Recommendation: ship the proper FSM** — Monaco gives us exact diffs, so it's cheap and correct.

---

## 7. Cursors, selection & presence (awareness)

Two layers:

1. **Local caret survival across remote applies.** When we apply a remote op, transform the local caret offset through the op's components (retain advances, insert before caret shifts right, delete before caret shifts left) — identical math to `radzen-collab-adapter.js._transformCursor`. Then restore via `editor.setPosition(model.getPositionAt(newOffset))`.

2. **Remote presence (optional but recommended).** Send our selection as awareness:
   - On `onDidChangeCursorSelection`, throttle (~50 ms) and send `AwarenessRequest{ documentId, dataJson }` where `dataJson` is e.g. `{"name":"Ada","color":"#e91e63","anchor":123,"head":140}`.
   - On `ReceiveAwarenessEvent`, render each remote peer's selection with Monaco **decorations** (`deltaDecorations`) for the range and a thin caret widget. Map their stored offsets through any ops applied since (or just accept eventual correction on their next awareness tick).
   - On `PeerDisconnectedEvent`, remove that peer's decorations.

Awareness is fire-and-forget (no correlation id, no persistence) — exactly how the server treats it.

---

## 8. Lifecycle

### 8.1 Join / seed

```
1. ws = new WebSocket("wss://host/ws")
2. on open → send WebSocketMessage{ messageType: 0 /*JoinRequest*/,
                                    correlationId,
                                    joinRequest: { documentId, documentType:"text", clientProtoVersion:1 } }
3. on JoinResponse{ revision, snapshot(base64), awareness }:
     text = JSON.parse(utf8(base64Decode(snapshot))).content
     model = monaco.editor.createModel(text, "plaintext")
     model.setEOL(LF)
     editor.setModel(model)
     state.revision = revision
     attach onDidChangeModelContent (guarded) and onDidChangeCursorSelection
     render initial awareness
```

The model is created **from** the snapshot, so there is no caret-reset problem (unlike the opaque-HTML adapter that had to seed via a side channel).

### 8.2 Local edit

```
onDidChangeModelContent(e):
   if (applyingRemote) return;                  // ignore our own remote applies
   op = fromMonacoChanges(e.changes)
   if (isNoOp(op)) return
   feed op into the client-OT FSM (§6)  →  maybe send OpRequest{ payload: base64(utf8(JSON(op))), baseRevision }
```

### 8.3 Reconnect / resync

WebSocket drops happen. On `onclose`:

- Exponential-backoff reconnect.
- On reconnect, **re-Join**. The `JoinResponse` gives a fresh authoritative snapshot + revision. **Reconcile**: compute a diff between the current Monaco text and the fresh snapshot (Monaco has no built-in OT, but we can diff with a simple LCS or just `model.setValue` if we choose server-wins). Safest default: **server-wins re-seed** — replace the model content with the snapshot (warn if local un-acked edits would be lost; in practice the in-flight op was likely already committed before the drop). A more advanced version replays un-acked `inflight`/`buffer` against the new base after transformation.
- Drop stale `inflight`/`buffer` unless we implement replay.

### 8.4 Teardown

`editor.dispose()`, `model.dispose()`, remove listeners, `ws.close(1000)`. Clear awareness decorations.

---

## 9. Server-side considerations (small, optional)

The design needs **no** server change to function, but two optional tweaks make the JS client nicer:

### 9.1 String enums on the wire

The enum currently serializes as integers. Adding `Converters = { new JsonStringEnumConverter() }` to the WebSocket transport's `JsonOptions` would let JS send `"messageType":"JoinRequest"` instead of `0`. **Trade-off:** this changes the wire format for *all* WS clients (including the existing .NET `WebSocketOpStreamClient`), so either coordinate the change or keep integers and document them in the JS client. **Recommendation: keep integers** (zero risk) and centralize them as named constants in `opstream-ws.js`.

### 9.2 Receive-buffer size

`WebSocketTransport.ReceiveTextAsync` reads in 4 KB chunks but loops to end-of-message, so large pastes are fine. No change needed; noted for awareness.

### 9.3 Auth

The collaboration path authorizes joins/ops via the host's `IDocumentAuthorizer` (separate from the management plane). The browser must present whatever credential the host expects — typically a token in the WebSocket URL query string or a cookie on the upgrade request. That is host policy; the JS client just needs a hook to attach it to the `ws://…` URL.

---

## 10. File / deliverable layout

A self-contained sample, e.g. `samples/MonacoCollaborativeJs/`:

```
wwwroot/
  index.html              // loads Monaco (CDN) + the three modules, creates the editor
  js/
    opstream-ws.js        // WebSocket envelope, base64, correlation, reconnect
    ot-text.js            // TextOp apply/transform/compose + fromMonacoChanges
    monaco-collab.js      // public API: attachCollab(editor, { url, documentId })
```

Public API sketch:

```js
import { attachCollab } from "./js/monaco-collab.js";

const editor = monaco.editor.create(document.getElementById("editor"), { language: "plaintext" });
const session = attachCollab(editor, {
  url: "wss://localhost:5001/ws",
  documentId: "monaco-doc-1",
  presence: { name: "Ada", color: "#e91e63" },   // optional
  getAuthToken: () => localStorage.token,         // optional
});
// session.dispose() to tear down.
```

The host app only needs to map the WS endpoint (already wired by `WebSocketTransportExtensions`) and ensure CORS/auth for the upgrade.

---

## 11. Testing strategy

- **OT conformance (JS ↔ C#).** Port the engine's existing test vectors (`TextOtEngineTests`, `TextOtFuzzerTests`) into a JS test suite for `ot-text.js`. Assert `apply`, `transform`, and `compose` produce **byte-identical** JSON to the C# engine for the same inputs — this is the correctness keystone, because the client transforms locally.
- **Convergence property test.** Two simulated clients issue random concurrent ops against an in-process fake server (or the real one via WS); assert both converge to the same text and to the server's snapshot. Mirror the C# fuzzers.
- **Monaco encoding round-trip.** For scripted `onDidChangeModelContent` events (single edit, multi-cursor, find-&-replace, paste, CRLF paste, emoji/surrogate pairs), assert `fromMonacoChanges` → `apply` reproduces Monaco's resulting text.
- **Echo suppression.** Author an op, receive its own `ReceiveOpEvent`, assert no double-apply and caret unchanged.
- **Reconnect/resync.** Kill the socket mid-edit, reconnect, assert convergence to server snapshot.
- **Cursor transform.** Remote insert/delete before/after/at the caret keeps the caret on the right character.
- **Manual matrix.** Two browsers, same doc: simultaneous typing at the same offset, large paste vs. typing, rapid undo/redo.

---

## 12. Risks & mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| JS OT diverges from C# engine semantics | Silent corruption | §11 conformance tests against the C# vectors; identical priority/tie-break rules |
| Transform priority mismatch with server | Insert-at-same-offset reorders differently | Pin and document the priority; cover with convergence tests |
| EOL (`\r\n`) length mismatch | Off-by-N offsets | Pin model EOL to LF; normalize snapshot |
| `applyEdits` offset shifting within a batch | Wrong placement | Compute ranges from pre-edit model, apply descending; or apply one-by-one back-to-front |
| Enum-as-integer wire format brittleness | Hard-to-debug 400s | Centralize numeric constants; (optionally) `JsonStringEnumConverter` server-side |
| Lost un-acked edits on reconnect | Minor data loss | Default server-wins re-seed; optional replay of `inflight`/`buffer` |
| Auth on WS upgrade | Unauthorized access / broken connect | `getAuthToken` hook; host decides query-string vs cookie |

---

## 13. Incremental rollout

1. **`ot-text.js` + conformance tests** — the riskiest piece, built and proven against the C# engine vectors *first*, in isolation.
2. **`opstream-ws.js`** — connect, Join, decode snapshot, render read-only Monaco from the snapshot (no editing yet). Proves the wire format end-to-end.
3. **Local→remote** — encode `onDidChangeModelContent`, send ops, see them land in a second browser. Echo suppression.
4. **Client-OT FSM** — inflight/buffer + transform on `ReceiveOpEvent`. Concurrency correctness.
5. **Cursor transform + presence** — caret survival, then optional remote-cursor decorations/awareness.
6. **Reconnect/resync + teardown.**
7. **Package as `samples/MonacoCollaborativeJs`.**

Steps 1–4 deliver a correct collaborative plain-text editor; 5–7 are polish.

---

## 14. Summary

Monaco maps onto OpStream's `TextOtEngine` almost perfectly: its change events are already retain/insert/delete diffs, and the existing **WebSocket transport** speaks JSON we can produce from the browser with no .NET on the client. The work is three small vanilla-JS modules — a faithful **JS port of `TextOp`** (the one piece that must be provably identical to the server engine), a thin **WebSocket envelope** layer, and a **Monaco glue** layer running the standard client-OT state machine. No Blazor, no server changes required (two optional server niceties noted). The plan front-loads the only real risk — OT conformance — and proves it against the engine's existing test vectors before anything else is built.
