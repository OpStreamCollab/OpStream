# Text OT Engine

Operational-transformation engine for plain-text co-editing. This is the
default engine registered by `AddOpStream()` under the document type
`"text"`.

## When to use

- Source-code or markdown collaborative editing.
- Editors that expose **insert / retain / delete** primitives natively:
  Monaco, CodeMirror, contenteditable, ProseMirror plain-text mode.
- Anything where edits are characterized by their position in a string.

For attributed text (bold / italic / lists), use [Rich Text](rich-text.md)
instead.

## Types

`TDoc` = `TextDocument` — wraps a single `Content` string.

`TOp` = `TextOp` — a sequence of `TextOpComponent`:

| Component | Effect |
|---|---|
| `Retain(count)` | Keep the next `count` characters as-is. |
| `Insert(text)` | Insert `text` at the current cursor. |
| `Delete(count)` | Delete the next `count` characters. |

Cursor position is implicit; the components are walked left-to-right.

## Worked example

```csharp
// State: "Hello"
// Goal:  "Hello, world"

var op = new TextOp(new TextOpComponent[]
{
    new Retain(5),
    new Insert(", world")
});

var newState = engine.Apply(state, op);
// newState.Content == "Hello, world"
```

A concurrent op from another peer is rebased automatically:

```csharp
// Alice and Bob both base-rev N. Alice prepends "Hey ", Bob appends "!".
// Both ops arrive at the server.
// Alice's lands first; Bob's is transformed against Alice's:
var bobTransformed = engine.Transform(bob, alice, TransformPriority.ExistingWins);
// Bob's "append at length 5" becomes "append at length 9" — correct, no clash.
```

## Registration

```csharp
services.AddOpStream();                    // already registers "text"
```

To register multiple text document types (e.g. one per file extension):

```csharp
services.AddOpStream()
    .AddEngine<TextDocument, TextOp, TextOtEngine>("text/markdown")
    .AddEngine<TextDocument, TextOp, TextOtEngine>("text/typescript");
```

## Client wire shape

```json
{
  "components": [
    { "type": "retain", "count": 5 },
    { "type": "insert", "text": ", world" }
  ]
}
```

Use the constants in `OpStreamConstants` on the .NET side or copy the
discriminator strings on JS / native clients.

## Undo / redo

`TextOtEngine.Invert(op, preState)` reconstructs the inverse of any op
using the original string for the bytes that were deleted. This makes it
fully compatible with [`UndoRedoEngine<TextDocument, TextOp>`](undo-redo.md).
No `RestampToWin` override is needed — OT engines are timestamp-free.

## Limitations

- **Single line of bytes.** No concept of paragraphs or lines beyond `\n`.
- **No attributes.** If you need bold / italic / lists, use
  [Rich Text](rich-text.md).
- **Position-based.** Caret bookkeeping for long-distance moves can get
  large under heavy concurrency — for very long documents, snapshot more
  aggressively (see [Snapshots](../operations/snapshots.md)).
