using OpStream.Shared.Messages;
using System.Text.Json;

namespace OpStream.Server.Engine.Form;

/// <summary>
/// Engine for two-way bound forms (settings dialogs, questionnaires, form-builder UIs).
/// <para>
/// Strategy is field-level LWW: each field is a register identified solely by its name,
/// and conflicts are resolved by <c>(Timestamp, PeerId)</c>. No paths, no array splicing —
/// operations on different fields commute by construction, which is why <see cref="Transform"/>
/// is identity.
/// </para>
/// <para>
/// Despite the name ending in <c>OtEngine</c> for codebase consistency with <c>TextOtEngine</c>
/// and <c>RichTextEngine</c>, the algorithm is closer to a flat <c>JsonCrdt</c>. The naming
/// matches the original design vocabulary in the README rather than the underlying technique.
/// </para>
/// </summary>
public class FormOtEngine : IOpEngine<FormDocument, FormOpBatch>
{
    public FormDocument Apply(FormDocument state, FormOpBatch batch)
    {
        var fields = new Dictionary<string, FieldRegister>(state.Fields);

        foreach (var op in batch.Operations)
        {
            switch (op)
            {
                case SetFieldOp set:
                    ApplyField(fields, set.FieldName, set.Value, set.Timestamp, set.PeerId);
                    break;
                case ClearFieldOp clear:
                    ApplyField(fields, clear.FieldName, JsonNull(), clear.Timestamp, clear.PeerId);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported form op: {op.GetType().Name}");
            }
        }

        return new FormDocument(fields);
    }

    /// <summary>
    /// LWW per field — different field names are independent, same field name resolves by
    /// (Timestamp, PeerId). No transport-level rebase is required.
    /// </summary>
    public FormOpBatch? Transform(FormOpBatch incoming, FormOpBatch existing, TransformPriority priority) => incoming;

    /// <summary>
    /// Compose is intentionally unsupported: collapsing two ops on the same field would lose
    /// the per-op LWW timestamps that other replicas need to converge.
    /// </summary>
    public FormOpBatch? Compose(FormOpBatch a, FormOpBatch b) => null;

    public FormOpBatch Invert(FormOpBatch batch, FormDocument preState)
    {
        var inverted = new List<FormOp>(batch.Operations.Count);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Reverse walk so a batch that touches the same field twice undoes in the correct order.
        for (int i = batch.Operations.Count - 1; i >= 0; i--)
        {
            var op = batch.Operations[i];
            long safeTs = Math.Max(now, op.Timestamp + 1);

            string fieldName = op switch
            {
                SetFieldOp s => s.FieldName,
                ClearFieldOp c => c.FieldName,
                _ => throw new NotSupportedException($"Cannot invert form op: {op.GetType().Name}")
            };

            if (preState.Fields.TryGetValue(fieldName, out var prev))
            {
                if (prev.Value.ValueKind == JsonValueKind.Null)
                    inverted.Add(new ClearFieldOp(fieldName, safeTs, op.PeerId));
                else
                    inverted.Add(new SetFieldOp(fieldName, prev.Value, safeTs, op.PeerId));
            }
            else
            {
                // Field didn't exist pre-op — undo clears it so future reads see "never set".
                // We can't truly delete the register without breaking convergence, but writing
                // a null tombstone is the closest reversible operation.
                inverted.Add(new ClearFieldOp(fieldName, safeTs, op.PeerId));
            }
        }

        return new FormOpBatch(inverted);
    }

    public bool IsNoOp(FormOpBatch op) => op.Operations.Count == 0;

    /// <summary>
    /// Rewrites every op's <c>Timestamp</c> to <c>max(maxRegisterTimestamp, now) + 1</c> so a
    /// cached inverse from <c>UndoRedoEngine</c> beats any concurrent winner that landed since
    /// record time. Mirrors the override in <c>JsonCrdtEngine</c> / <c>TableCrdtEngine</c>.
    /// </summary>
    public FormOpBatch RestampToWin(FormOpBatch op, FormDocument currentState)
    {
        if (op.Operations.Count == 0) return op;

        long max = 0;
        foreach (var reg in currentState.Fields.Values)
        {
            if (reg.Timestamp > max) max = reg.Timestamp;
        }
        long newTs = Math.Max(max, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) + 1;

        var rewritten = new List<FormOp>(op.Operations.Count);
        foreach (var fop in op.Operations)
        {
            rewritten.Add(fop switch
            {
                SetFieldOp s => s with { Timestamp = newTs },
                ClearFieldOp c => c with { Timestamp = newTs },
                _ => fop
            });
        }
        return new FormOpBatch(rewritten);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────

    private static void ApplyField(Dictionary<string, FieldRegister> fields, string fieldName, JsonElement value, long ts, string peerId)
    {
        if (fields.TryGetValue(fieldName, out var existing))
        {
            if (!LwwWins(ts, peerId, existing.Timestamp, existing.PeerId)) return;
        }
        fields[fieldName] = new FieldRegister(value, ts, peerId);
    }

    private static bool LwwWins(long incomingTs, string incomingPeer, long existingTs, string existingPeer)
    {
        if (incomingTs > existingTs) return true;
        if (incomingTs < existingTs) return false;
        return string.CompareOrdinal(incomingPeer, existingPeer) > 0;
    }

    private static JsonElement JsonNull()
    {
        using var doc = JsonDocument.Parse("null");
        return doc.RootElement.Clone();
    }
}
