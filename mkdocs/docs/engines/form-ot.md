# Form OT Engine

Lightweight field-level LWW engine for two-way bound forms — settings
dialogs, questionnaires, wizard steps.

## When to use

- Settings / preferences forms.
- Quiz / survey responses with concurrent multi-user editing.
- Any UI where the document is a flat bag of named fields.

For nested / hierarchical data, prefer [JSON CRDT](json-crdt.md) or
[Tree CRDT](tree-crdt.md). For spreadsheet-shaped data, use
[Table CRDT](table-crdt.md).

!!! info "Naming"
    Despite the `OtEngine` suffix (kept for codebase consistency with
    `TextOtEngine` and `RichTextEngine`), the algorithm is technically a
    flat LWW-per-field CRDT. `Transform` is identity; convergence comes
    from per-field LWW resolution at `Apply` time.

## Types

`TDoc` = `FormDocument` — `Dictionary<fieldName, FieldRegister>`.
`FieldRegister = (Value, Timestamp, PeerId)`.

| State of a field | Means |
|---|---|
| Absent from `Fields` | Never set. |
| Register with `Value.ValueKind == Null` | Explicitly cleared. |
| Register with any other value | Currently set. |

`TOp` = `FormOpBatch` wrapping:

| Op | Effect |
|---|---|
| `SetFieldOp(fieldName, value, ts, peerId)` | LWW write a field. |
| `ClearFieldOp(fieldName, ts, peerId)` | LWW write a JSON `null` to a field. |

## Worked example

```csharp
var engine = new FormOtEngine();
var batch = FormOpBatch.Create(
    new SetFieldOp("email",   JsonString("alice@example.com"), 1, "p"),
    new SetFieldOp("optInNL", JsonBool(true),                  2, "p"));

var state = engine.Apply(new FormDocument(), batch);
```

## Typed wrapper

`FormOtEngine<TForm>` lets you build batches from a domain POCO:

```csharp
public record SettingsForm(string Email, bool OptInNewsletter, int ItemsPerPage);

var typed = new FormOtEngine<SettingsForm>();

// Submit the whole form atomically — one SetFieldOp per top-level property.
var batch = typed.BuildSetFromObject(
    new SettingsForm("alice@example.com", true, 50),
    timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    peerId: "peer1");

// Or a single field
var op = typed.BuildSetField("ItemsPerPage", 100, ts, peerId);

// Read back into the POCO
var current = typed.Read(state);
```

## Registration

```csharp
services.AddOpStream()
    .AddEngine<FormDocument, FormOpBatch, FormOtEngine>("form");
```

## Undo / redo

`FormOtEngine` overrides `RestampToWin` so cached inverses beat the
current LWW winners. Fully compatible with [`UndoRedoEngine`](undo-redo.md).

## Limitations

- **No arrays / nested structures.** A field can store an array as a
  JSON value, but the entire field is one LWW register — collaborative
  reordering inside the array isn't safe. For that, use Tree CRDT.
- **No field-level validation.** Use OpStream's `IOpValidator<FormOpBatch>`
  to reject bad batches before they get applied — see
  [Builder API: AddValidator](../reference/builder-api.md#addvalidator).
