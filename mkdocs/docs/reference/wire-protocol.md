# Wire protocol

OpStream's three transports (SignalR, WebSockets, gRPC) all serialize
the same logical messages. Engines determine the **shape of the op
payload**; the envelope around it is uniform.

## Protocol versioning

A single integer:

```csharp
public static class ProtocolVersions { public const int Current = 1; }
```

Clients send their `ProtocolVersion` with `JoinDocument`. A mismatch
rejects the join with `"UnsupportedProtocol"`.

## Logical messages

### `JoinDocument` (client → server)

```json
{ "documentId": "doc-1", "documentType": "text", "protocolVersion": 1 }
```

Returns `SessionJoinResult`:

```json
{
  "revision": 14,
  "snapshot": "<base64 of JSON-serialized TDoc>",
  "pendingOps": [],
  "currentAwareness": [
    { "peerId": "p-3", "data": { ... }, "lastUpdated": "..." }
  ]
}
```

### `SendOp` (client → server)

```json
{ "documentId": "doc-1", "payload": "<base64>", "baseRevision": 14 }
```

Where `payload` is `JsonSerializer.SerializeToUtf8Bytes(TOp,
OpStreamJsonOptions.Default)`. Returns `OpApplyResult`:

```json
{ "success": true, "newRevision": 15 }
// or on rejection:
{ "success": false, "newRevision": 14, "errorMessage": "Forbidden: ..." }
```

### `UpdateAwareness` (client → server)

```json
{ "documentId": "doc-1", "data": { ... } }
```

Returns the freshly-stored `AwarenessState`.

### `ReceiveOp` (server → client)

```json
{ "documentId": "doc-1", "payload": "<base64>", "revision": 15 }
```

The `payload` is the **server-transformed** op — already rebased
through OT / CRDT against any concurrent ops the peer hadn't seen.

### `ReceiveAwareness` (server → client)

```json
[ { "peerId": "p-3", "data": { ... }, "lastUpdated": "..." }, ... ]
```

Sent on join with the full live snapshot; later deltas come via
`ReceiveAwarenessUpdate` (a single `AwarenessState`).

### `PeerDisconnected` (server → client)

```json
{ "peerId": "p-7" }
```

## Op payloads per engine

The bytes inside `payload` are engine-specific. Use the discriminator
`"type"` field on each polymorphic op variant. Examples:

### TextOp

```json
{ "components": [
    { "type": "retain", "count": 5 },
    { "type": "insert", "text": ", world" }
] }
```

### RichTextOp

```json
{ "components": [
    { "type": "retain", "count": 5 },
    { "type": "retain", "count": 5, "attributes": { "bold": true } }
] }
```

### JsonOpBatch

```json
{ "operations": [
    { "type": "set", "path": "user.name", "value": "Alice", "timestamp": 100, "peerId": "p-1" }
] }
```

### TreeOpBatch

```json
{ "operations": [
    { "type": "move", "nodeId": "A", "newParentId": "__root__",
      "newPosition": "m", "newPayload": null, "timestamp": 100, "peerId": "p-1" }
] }
```

### TableOpBatch

```json
{ "operations": [
    { "type": "set_cell", "rowId": "R1", "columnId": "C1",
      "value": "hello", "timestamp": 100, "peerId": "p-1" }
] }
```

### FormOpBatch

```json
{ "operations": [
    { "type": "set", "fieldName": "email", "value": "a@b.c",
      "timestamp": 100, "peerId": "p-1" }
] }
```

## JSON conventions

All serialization uses `OpStreamJsonOptions.Default`:

- camelCase property names.
- Polymorphic types via `[JsonDerivedType(..., "discriminator")]`.
- Enums as strings (lowercase).
- `JsonElement` preserved verbatim for opaque payloads.

A non-.NET client should match these conventions exactly. The simplest
way is to round-trip a known-good payload through your client and
compare bytes to the server's expected shape.

## Backplane envelope (cross-node only)

If you're integrating directly with the backplane (e.g. writing a
custom transport that forwards into OpStream), the message types are:

| Type | Direction | Payload |
|---|---|---|
| `OpStreamConstants.BackplaneMessages.OpApplied` | Pub/sub fan-out | `OpAppliedBackplanePayload(opBytes, revision)` |
| `OpStreamConstants.BackplaneMessages.ReceiveAwarenessUpdate` | Pub/sub fan-out | Serialized `AwarenessState` |
| `OpStreamConstants.BackplaneCommands.JoinDocument` | RPC | `JoinRequestData` |
| `OpStreamConstants.BackplaneCommands.ApplyOp` | RPC | `ApplyOpRequestData` |
| `OpStreamConstants.BackplaneCommands.UpdateAwareness` | RPC | `UpdateAwarenessRequestData` |

These are **internal** for normal use. Build your own client transports
against the three logical messages above, not the backplane envelope.
