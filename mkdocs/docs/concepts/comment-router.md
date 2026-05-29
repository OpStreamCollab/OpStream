# Comment Router

The `CommentRouter` is the internal component that manages the **lifecycle of
[comments](comments.md)**: create, edit, resolve, and delete. It is the comment-plane
sibling of the [Document Router](document-router.md), and like it, it is registered as a
singleton by `services.AddOpStream()`.

## Why comments need their own router

A comment's [anchor](comments.md#anchors) must be resolved against the document's
**current [revision](revision.md)** at the exact moment of creation. If that happened on a
node that didn't own the document, the revision could change underneath it, leaving the
anchor pointing at the wrong place.

So the Comment Router **routes every mutation to the node that owns the document** (the
same [ownership model](../operations/backplane.md#ownership-model) used by the collaboration
path). On the owner, the mutation is applied while holding the [session](session.md) apply
lock, which guarantees the anchor is fixed against a revision that cannot move mid-operation.

## What it guarantees

| Guarantee | How |
|---|---|
| **Atomic anchoring** | Mutations run under the session lock on the owning node, so the anchored-at revision is consistent. |
| **Cluster-wide fan-out** | After a mutation commits, a `CommentCreated` / `CommentUpdated` / `CommentDeleted` message is broadcast over the [backplane](../operations/backplane.md) so every peer on the document is notified. |
| **Durable persistence** | Changes are written through the configured [`ICommentStore`](../storage/index.md) (EF Core, MongoDB, or Redis). |
| **Cascade on delete** | Deleting a root comment removes its replies. |

## See also

- [Comment](comments.md) — the data and anchoring model the router operates on.
- [Document Router](document-router.md) — the collaboration-plane equivalent.
- [Backplane](../operations/backplane.md) — the fan-out fabric for comment events.
