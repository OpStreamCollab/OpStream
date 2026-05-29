# Peer

A **peer** is a single connected client — typically one browser tab or one desktop/app
instance. It is the unit of concurrency OpStream reasons about: not "a user", but "a
connection".

## Peer id

The [transport](../transports/index.md) assigns each connection a **peer id** at connect
time. Everything ephemeral is keyed by it:

- [Awareness](../engines/awareness.md) state (cursor, selection, "is typing").
- Fan-out targeting (an op accepted from one peer is broadcast to all *other* peers).
- Disconnect cleanup (the peer's awareness is dropped when its connection drops).

## Why connections, not users

Multiple peers under the same user account are treated as **independent** for concurrency
purposes. If you open the same document in two tabs, that's two peers — each with its own
cursor and its own in-flight [ops](../reference/interfaces.md). This is intentional: the
two tabs can legitimately diverge for a moment and must be reconciled exactly as if they
were two different people, so the [revision](revision.md)-based rebase logic applies
uniformly.

Identity and permissions are a separate layer: your [`IDocumentAuthorizer`](../operations/authorization.md)
maps the underlying user to access rights, while the peer id only tracks the live
connection.

## Lifecycle

1. Client connects through a transport → peer id assigned.
2. Peer joins a document → enters the [Session](session.md), receives current state +
   other peers' awareness.
3. Peer sends ops and awareness; receives fan-out from other peers.
4. Peer disconnects → awareness evicted, other peers notified.

## See also

- [Awareness](../engines/awareness.md) — the ephemeral state each peer publishes.
- [Session](session.md) — where peers on the same document meet.
- [Transport](../transports/index.md) — assigns the peer id and carries its traffic.
