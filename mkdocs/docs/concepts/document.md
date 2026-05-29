# Document

A **document** is whatever your application calls "the thing two users co-edit": a
markdown file, a Notion page, a settings dialog, a spreadsheet, a CAD model. OpStream is
deliberately agnostic about its semantics — it only needs you to pick an
[engine](../engines/index.md) that knows how to merge concurrent edits on that shape.

## Anatomy

Every document is described by four pieces of metadata:

| Part | Type | Description |
|---|---|---|
| **Document id** | `string` | Opaque, caller-supplied identifier. Scoped per tenant on the server via [Multitenancy](../operations/multitenancy.md) — the client only ever sees its *local* id. |
| **Document type** | `string` | Discriminator (`"text"`, `"rich-text"`, `"tree"`, …) that tells the [Document Router](document-router.md) which [engine](../engines/index.md) and session factory to use. |
| **State** | `TDoc` | The strongly-typed value the engine produces after applying ops (e.g. a `TextDocument`). |
| **Revision** | `long` | A monotonic counter, incremented once per accepted [op](../reference/interfaces.md). See [Revision](revision.md). |

## Lifecycle

1. A client connects through a [transport](../transports/index.md) and **joins** a
   document by id and type.
2. The router authorizes the join, resolves (or creates) the owning node, and spins up a
   [Session](session.md) if one isn't already live.
3. The session loads the latest [Snapshot](../operations/snapshots.md) and replays any
   ops after it to rebuild current state.
4. Clients exchange [ops](../reference/interfaces.md); each accepted op bumps the revision
   and is persisted to [Storage](../storage/index.md).
5. After an idle timeout the session closes; the document lives on in storage until it is
   explicitly deleted through the management plane.

## Identity vs. physical storage

The id a client uses ("local id") is not necessarily the key under which bytes are stored.
The server **globalizes** ids with the tenant prefix, and [branches](branching.md) add a
further suffix — the physical convention is `tenant:#:localName@branchId`. This indirection
is what keeps tenants isolated and lets a single logical document host many divergent
branches.

## See also

- [Document Router](document-router.md) — the entry point that routes every document call.
- [Session](session.md) — the in-memory home of an open document.
- [Engines overview](../engines/index.md) — how a document's shape is merged.
