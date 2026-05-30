# Recipe: Collaborative settings dialog

!!! example "Try it live"
    **▶ [Live demo](https://hostdemo.opstream.stream/samples/form)** ·
    **[&lt;/&gt; Source](https://github.com/OpStreamCollab/OpStream/tree/main/samples/BlazorFormEditor)** ·
    [all samples](../samples.md)

Two users open the same project's settings dialog. They edit different
fields simultaneously, and the changes propagate live without one
user's tab clobbering the other's.

## What we'll use

- [`FormOtEngine<TForm>`](../engines/form-ot.md) — flat per-field LWW.
- SignalR transport.
- Storage of choice (SQL Server in this example).
- Awareness for "Bob is editing this field" highlights.

## Domain model

```csharp
public record ProjectSettings(
    string Name,
    string Description,
    bool   IsPublic,
    int    MaxMembers,
    string DefaultBranch);
```

## Server

```csharp
using OpStream.Server.Engine.Form;

builder.Services
    .AddOpStream()
    .UseSqlServer(builder.Configuration.GetConnectionString("OpStream")!)
    .UseAuthorization<MyAuthorizer>()
    .AddEngine<FormDocument, FormOpBatch, FormOtEngine>("project-settings")
    .AddSignalRTransport();
```

## Client — initial load

```csharp
var typed = new FormOtEngine<ProjectSettings>();
var join  = await client.JoinAsync($"settings:{projectId}", "project-settings");
var doc   = JsonSerializer.Deserialize<FormDocument>(join.Snapshot)!;
var view  = typed.Read(doc) ?? new ProjectSettings("", "", false, 10, "main");
```

## Per-field updates

```csharp
async Task OnNameChangedAsync(string newName)
{
    var op = typed.BuildSetField("Name", newName,
        timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        peerId:    myPeerId);

    var batch = new FormOpBatch(new FormOp[] { op });
    await client.SendOpAsync(documentId,
        JsonSerializer.SerializeToUtf8Bytes(batch),
        baseRevision);
}
```

Each field change is **one** `SetFieldOp`. Two users editing
different fields don't conflict at all — the LWW resolution runs
per-field. Two users editing the **same** field converge to the
higher-timestamp value.

## Receiving updates

```csharp
client.OnReceiveOp += (docId, payload, newRev) =>
{
    var batch = JsonSerializer.Deserialize<FormOpBatch>(payload)!;
    doc = engine.Apply(doc, batch);   // engine = injected FormOtEngine
    view = typed.Read(doc) ?? view;
    StateHasChanged();
    return Task.CompletedTask;
};
```

## "Bob is editing this field" highlight

Use [awareness](../engines/awareness.md) to broadcast which field has
focus:

```csharp
async Task OnFieldFocus(string fieldName)
{
    await client.SendAwarenessAsync(documentId,
        JsonSerializer.SerializeToElement(new
        {
            focusedField = fieldName,
            name = currentUserName,
            color = currentUserColor,
        }));
}
```

Then highlight the field per peer:

```razor
<div class="form-field @(IsBeingEditedByOther("Name") ? "remote-focus" : "")">
    <label>Name</label>
    <input value="@view.Name" @onfocus="@(() => OnFieldFocus("Name"))" ... />
</div>

@code {
    bool IsBeingEditedByOther(string field)
        => livePeers.Any(p => p.PeerId != myPeerId && p.Data.GetProperty("focusedField").GetString() == field);
}
```

## Whole-form submit

If users hit "Save" with everything at once instead of editing
field-by-field, emit one batch with all changed fields:

```csharp
async Task SaveAsync(ProjectSettings updated)
{
    var batch = typed.BuildSetFromObject(updated,
        timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        peerId:    myPeerId);

    await client.SendOpAsync(documentId,
        JsonSerializer.SerializeToUtf8Bytes(batch),
        baseRevision);
}
```

## Validation

Wire an `IOpValidator<FormOpBatch>` to reject malformed batches before
they hit storage:

```csharp
public sealed class SettingsValidator : IOpValidator<FormOpBatch>
{
    public ValueTask<bool> ValidateAsync(OpValidationContext<FormOpBatch> ctx, CancellationToken ct)
    {
        foreach (var op in ctx.Op.Operations)
        {
            if (op is SetFieldOp set && set.FieldName == "MaxMembers")
            {
                if (!set.Value.TryGetInt32(out var n) || n < 1 || n > 10_000) return new(false);
            }
        }
        return new(true);
    }
}

builder.Services.AddOpStream()
    /* ... */
    .AddValidator<FormOpBatch, SettingsValidator>();
```

The router rejects invalid batches with `OpApplyResult.Success = false`;
your UI gets a clear error rather than a silent storage write.
