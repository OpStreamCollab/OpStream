using OpStream.Constants;
using OpStream.Shared.Messages;
using System.Text.Json;

namespace OpStream.Server.Engine.Form;

/// <summary>
/// Strongly-typed facade over <see cref="FormOtEngine"/>: callers express the form with a
/// domain POCO / record <typeparamref name="TForm"/> instead of raw <see cref="JsonElement"/>s.
/// <para>
/// Two helpers stand out: <see cref="BuildSetFromObject"/> diffs a typed instance into a batch
/// of <see cref="SetFieldOp"/>s, and <see cref="Read"/> hydrates a <see cref="FormDocument"/>
/// back into <typeparamref name="TForm"/>. The runtime engine is still the untyped core so the
/// wire format and storage stay uniform across clients.
/// </para>
/// </summary>
public sealed class FormOtEngine<TForm> : IOpEngine<FormDocument, FormOpBatch> where TForm : class
{
    private readonly FormOtEngine _inner = new();
    private readonly JsonSerializerOptions _jsonOptions;

    public FormOtEngine(JsonSerializerOptions? jsonOptions = null)
    {
        _jsonOptions = jsonOptions ?? OpStreamJsonOptions.Default;
    }

    public FormDocument Apply(FormDocument state, FormOpBatch op) => _inner.Apply(state, op);
    public FormOpBatch? Transform(FormOpBatch incoming, FormOpBatch existing, TransformPriority priority) => _inner.Transform(incoming, existing, priority);
    public FormOpBatch? Compose(FormOpBatch a, FormOpBatch b) => _inner.Compose(a, b);
    public FormOpBatch Invert(FormOpBatch op, FormDocument preState) => _inner.Invert(op, preState);
    public bool IsNoOp(FormOpBatch op) => _inner.IsNoOp(op);
    public FormOpBatch RestampToWin(FormOpBatch op, FormDocument currentState) => _inner.RestampToWin(op, currentState);

    /// <summary>
    /// Serializes <paramref name="form"/> as a JSON object and emits one <see cref="SetFieldOp"/>
    /// per top-level property. Convenience for "save the whole form" submit flows.
    /// </summary>
    public FormOpBatch BuildSetFromObject(TForm form, long timestamp, string peerId)
    {
        var element = JsonSerializer.SerializeToElement(form, _jsonOptions);
        if (element.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"TForm must serialize to a JSON object; got {element.ValueKind}.", nameof(form));

        var ops = new List<FormOp>();
        foreach (var prop in element.EnumerateObject())
        {
            // Clone is mandatory: the JsonElement returned by EnumerateObject is backed by the
            // outer document; without Clone the op would hold a reference that becomes invalid
            // once `element` is collected.
            ops.Add(new SetFieldOp(prop.Name, prop.Value.Clone(), timestamp, peerId));
        }
        return new FormOpBatch(ops);
    }

    /// <summary>
    /// Builds a single typed <see cref="SetFieldOp"/>. Use when only one field changed
    /// — avoids the round-trip through the whole POCO.
    /// </summary>
    public SetFieldOp BuildSetField<TValue>(string fieldName, TValue value, long timestamp, string peerId)
    {
        var element = JsonSerializer.SerializeToElement(value, _jsonOptions);
        return new SetFieldOp(fieldName, element, timestamp, peerId);
    }

    /// <summary>
    /// Rehydrates the document into <typeparamref name="TForm"/>. Cleared fields (null
    /// registers) end up as JSON nulls in the intermediate object, which deserialize to the
    /// default value for the corresponding property.
    /// </summary>
    public TForm? Read(FormDocument document)
    {
        // Build a JSON object on the fly — cheaper than reflecting over TForm's properties
        // and lets the standard JsonSerializer apply naming policies, converters, etc.
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var kvp in document.Fields)
            {
                writer.WritePropertyName(kvp.Key);
                kvp.Value.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        stream.Position = 0;
        return JsonSerializer.Deserialize<TForm>(stream.ToArray(), _jsonOptions);
    }
}
