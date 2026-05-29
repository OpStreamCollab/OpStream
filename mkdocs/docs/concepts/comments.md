# Comment

A **comment** is a piece of collaborative feedback attached to a [document](document.md) at
a specific point in its timeline. Unlike [Awareness](../engines/awareness.md) — which is
ephemeral — comments are **durable**: they are persisted to a dedicated
[`ICommentStore`](../storage/index.md) and survive restarts, reloads, and op-log
compaction.

## What a comment carries

| Field | Purpose |
|---|---|
| **Id** | Stable identifier for the comment. |
| **Parent comment id** | Set for replies; `null` for a root comment. Enables threading. |
| **Author peer id** | Who created it. |
| **Body** | The free-text content. |
| **Anchor** | Where in the document it points (see below). Root comments carry an anchor; replies do not. |
| **Anchored-at revision** | The [revision](revision.md) the anchor was resolved against. |
| **Resolved-at / resolved-by** | Set when a thread is marked resolved. |
| **Is orphaned** | `true` when the anchored region was deleted by a later edit. |

## Threading

Comments form one-level threads: a **root** comment anchored to a location, plus any number
of **replies** that reference it via `ParentCommentId`. Deleting a root cascades to its
replies.

## Anchors

An **anchor** ties a comment to a position in the document — an offset range in text, a
JSON path in a structured document, and so on. Because the document keeps changing
underneath the comment, anchors are **rebased** automatically:

- After every applied [op](../reference/interfaces.md), a post-apply hook
  (`CommentAnchorRebaseHook`) shifts open anchors so they keep pointing at the same logical
  content.
- During [compaction](compaction.md), `CompactWithAnchorsService` rebases all open anchors
  *before* the op log is truncated, so no positional information is lost.
- If the anchored region is deleted entirely, the comment is marked **orphaned** rather
  than silently dropped.

Anchor rebasing is engine-specific. OpStream ships anchor engines for **text**,
**rich-text**, and **JSON** document types; each maps a document type to the logic that
knows how to move an offset or path forward through an edit.

## Mutations are owner-routed

Create / edit / resolve / delete all flow through the [Comment Router](comment-router.md),
which routes the mutation to the node owning the document so the anchor can be resolved
against the document's current revision **atomically under the session lock**.

## Transports

Comments are reachable over all three transports — SignalR, WebSockets
(`/comments-ws`), and gRPC (including a server-streaming `SubscribeComments` for live
push). Create / edit / resolve / delete events fan out to every peer on the document.

## See also

- [Comment Router](comment-router.md) — how comment mutations are routed and anchored.
- [Compaction](compaction.md) — why anchors are rebased before the log is trimmed.
- [Awareness](../engines/awareness.md) — the *ephemeral* counterpart to durable comments.
