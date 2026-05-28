# Rich Text Engine

Operational-transformation engine for **attributed** text — bold, italic,
links, lists, headings, anything modeled as Quill / ProseMirror / TipTap
deltas.

## When to use

- WYSIWYG editors with character-level formatting.
- Quill or ProseMirror / TipTap front-ends — the engine's wire format
  maps 1-to-1 to delta semantics.

For plain text without attributes, use the lighter
[Text OT engine](text-ot.md).

## Types

`TDoc` = `RichTextDocument` — wraps a sequence of `Insert` components
that together represent the current document.

`TOp` = `RichTextOp` — a delta of components:

| Component | Effect |
|---|---|
| `Insert(text, attributes?)` | Insert new content, optionally formatted. |
| `Retain(count, attributes?)` | Skip `count` characters; if `attributes` is set, apply them to that range. |
| `Delete(count)` | Remove `count` characters. |

`TextAttributes` is a dictionary `string → object?`. A `null` value means
**clear that attribute** for the range (so `{"bold": null}` removes bold).

## Worked example

```csharp
// Apply bold to characters 5..10 of "Hello world"
var op = new RichTextOp(new RichTextComponent[]
{
    new Retain(5),
    new Retain(5, new TextAttributes { ["bold"] = true }),
});

var newState = engine.Apply(state, op);
```

## Transform semantics

When two peers format the same range concurrently:

```csharp
// Alice sets italic, Bob sets color:red on the same range.
// Both attributes survive because they don't collide on the same key.
//
// Alice sets bold=true, Bob sets bold=false on the same range.
// TransformPriority.ExistingWins → Bob's pre-existing op wins.
```

See `TransformAttributes` in the source for the exact merge matrix.

## Registration

```csharp
services.AddOpStream()
    .AddEngine<RichTextDocument, RichTextOp, RichTextEngine>("rich-text");
```

## Mapping to Quill deltas

Quill's `Delta` shape is a direct counterpart:

| Quill | OpStream |
|---|---|
| `{ insert: "hi", attributes: {bold: true} }` | `new Insert("hi", new TextAttributes { ["bold"] = true })` |
| `{ retain: 5, attributes: {italic: true} }` | `new Retain(5, new TextAttributes { ["italic"] = true })` |
| `{ retain: 5, attributes: {bold: null} }` | `new Retain(5, new TextAttributes { ["bold"] = null })` |
| `{ delete: 3 }` | `new Delete(3)` |

A client adapter typically converts the editor's native delta into a
`RichTextOp` JSON payload before calling `SendOpAsync`.

## Undo / redo

`RichTextEngine.Invert(op, preState)` recovers both the deleted text **and
the original attributes** from the pre-state, so undo restores formatting
exactly. Fully compatible with [`UndoRedoEngine`](undo-redo.md); no
`RestampToWin` override needed.

## Limitations

- **Embeds (images, mentions) are opaque blobs.** Treat them as a single
  Insert with attributes; OT semantics still work but the engine doesn't
  introspect their structure.
- **Block-level structure isn't modeled directly.** If your editor sees
  paragraphs / headings as distinct nodes, model the block tree with
  [Tree CRDT](tree-crdt.md) and use Rich Text per leaf.
