# opstream-collab

Minimal browser client for [OpStream](https://github.com/OpStreamCollab/OpStream)
real-time collaboration. It owns everything that is identical across editors so
your integration only describes **how your editor turns edits into ops, and ops
back into edits**:

- **SignalR transport** + the `JoinDocument` handshake and reconnection.
- A **per-path coalescing outbox** (repeated edits to the same path while a send
  is in flight collapse to the latest — one op per drag gesture, not per tick).
- **Snapshot loading** and remote-op delivery, normalized to a simple `Op` shape.
- **Presence / awareness** (opt-in): roster, heartbeat, late-joiner convergence.
- **Anchored comments** (opt-in): create / list / resolve, casing normalized.

It is editor-agnostic: no DOM, no canvas, no diagram library. ~300 lines, one
peer dependency (`@microsoft/signalr`).

## Install

```bash
npm install opstream-collab @microsoft/signalr
```

## Usage

You provide `applyOps(ops, ctx)` (remote/snapshot ops → your editor) and call
`setPath()` / `delPath()` when the local editor changes.

```js
import { OpStreamSession } from 'opstream-collab';

const session = new OpStreamSession({
  url: '/collab',
  documentId: 'flow-demo',

  // presence + comments are opt-in
  presence: { name: 'User-42', color: '#3f7fff' },
  comments: { kind: 'gojs-node' },

  // remote + snapshot ops → your editor (you own ordering & echo-guarding)
  applyOps(ops, { fromSnapshot }) {
    for (const op of ops) {
      if (op.isDelete) editor.remove(idFrom(op.path));
      else editor.upsert(idFrom(op.path), op.value);
    }
  },

  onStatus:     (s)     => statusEl.textContent = s,
  onPeers:      (peers) => renderRoster(peers),
  onComments:   (list)  => renderComments(list),
  onRemoteEdit: (op)    => flashEdit(op.path, session.getPeer(op.peerId)),
});

await session.connect();

// local edits → broadcast
session.setPath('nodes.k-ab12', { text: 'Step', color: '#3f7fff' });
session.delPath('nodes.k-ab12');

// comments anchored to your editor's stable id
await session.addComment({ key: 'k-ab12' }, 'Looks good?');
```

### The `Op` shape handed to `applyOps`

| field       | meaning                                              |
|-------------|------------------------------------------------------|
| `path`      | register path, e.g. `"nodes.k-ab12"`                 |
| `value`     | the value (absent for deletes)                       |
| `isDelete`  | `true` for a tombstone                               |
| `peerId`    | sender's app peer id (`null` when from a snapshot)   |
| `timestamp` | ms epoch — lets order-sensitive editors sort         |

`applyOps` receives the **whole batch** so you can order it however your editor
needs (e.g. nodes before links) and wrap it in your own re-entrancy guard so
applying remote ops doesn't echo back as local edits.

## API

| member | description |
|---|---|
| `new OpStreamSession(opts)` | see options below |
| `await connect()` | connect, join, load snapshot, start presence/comments |
| `await disconnect()` | stop heartbeat + connection |
| `setPath(path, value)` / `delPath(path)` | queue a local op (coalesced) |
| `addComment(anchorData, body, parentId?)` | create an anchored comment |
| `resolveComment(id)` | resolve (close) a comment |
| `getPeer(peerId)` / `getPeerByConn(connId)` | presence lookup for op authors / comment authors |
| `peers` / `comments` | current roster / open comments |
| `revision` / `peerId` / `connection` | current state (read) |

### Constructor options

| option | default | description |
|---|---|---|
| `url` | — | SignalR hub URL (e.g. `"/collab"`) |
| `documentId` | — | shared document id |
| `documentType` | `"json"` | OpStream document/engine type |
| `protocolVersion` | `1` | wire protocol version |
| `applyOps(ops, ctx)` | — | apply remote/snapshot ops to your editor |
| `presence` | `null` | `{ name, color, … }` enables presence; `null` disables |
| `comments` | `null` | `{ kind }` enables anchored comments; `null` disables |
| `onStatus` / `onPeers` / `onComments` / `onRemoteEdit` | noop | callbacks |
| `peerId` | random | override the generated app peer id |
| `presenceHeartbeatMs` | `8000` | presence re-broadcast interval |

## Wire-contract notes (verified)

- Ops are **base64(JSON)**; the op discriminator is **`$type`** (`set` / `del`).
- Awareness is the **`ReceiveAwarenessUpdate`** event, one `AwarenessState`
  `{ peerId: <connId>, data, lastUpdated }` where `data` is your `UpdateAwareness`
  payload. The server **excludes the sender** and emits only on change, so peers
  re-broadcast when they first see a newcomer.
- Comments use `anchor: { kind, data }`, `authorPeerId` (the ConnectionId), and
  **resolved = `resolvedAt != null`** (there is no `anchorJson` / `isResolved`).

## License

MIT © jvera71
