using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpStream.Server.Engine.Form;

/// <summary>
/// A single field's value plus the LWW metadata that owns it. A cleared field is
/// represented as a register whose <see cref="Value"/> has
/// <see cref="JsonValueKind.Null"/> — distinguishable from "field never set" which
/// has no entry in <see cref="FormDocument.Fields"/>.
/// </summary>
public record FieldRegister(JsonElement Value, long Timestamp, string PeerId);

/// <summary>
/// The full form state: a flat <c>fieldName → FieldRegister</c> map. Deliberately
/// shallower than <c>JsonCrdtEngine</c>'s path-keyed model: forms are a single level
/// of named fields, no nesting, no array semantics. This makes the wire format and
/// the runtime checks materially cheaper for the common form / questionnaire / settings
/// dialog use-case.
/// </summary>
public record FormDocument(IReadOnlyDictionary<string, FieldRegister> Fields)
{
    public FormDocument() : this(new Dictionary<string, FieldRegister>()) { }
}

/// <summary>
/// Base type for every form operation. Each variant carries its own
/// <c>Timestamp</c> + <c>PeerId</c> pair so the LWW resolution inside Apply is uniform.
/// </summary>
[JsonDerivedType(typeof(SetFieldOp), "set")]
[JsonDerivedType(typeof(ClearFieldOp), "clr")]
public abstract record FormOp(long Timestamp, string PeerId);

/// <summary>Writes <paramref name="Value"/> into the field, subject to LWW.</summary>
public record SetFieldOp(string FieldName, JsonElement Value, long Timestamp, string PeerId) : FormOp(Timestamp, PeerId);

/// <summary>
/// Clears the field by writing a JSON <c>null</c> register. Distinct from "never set":
/// downstream consumers can tell the difference between an absent field and one that
/// was intentionally cleared.
/// </summary>
public record ClearFieldOp(string FieldName, long Timestamp, string PeerId) : FormOp(Timestamp, PeerId);

/// <summary>
/// Bundle of form ops applied atomically — same batched shape as the other engines so
/// transport / storage code stays uniform.
/// </summary>
public record FormOpBatch(IReadOnlyList<FormOp> Operations)
{
    public static FormOpBatch Create(params FormOp[] ops) => new(ops);
}
