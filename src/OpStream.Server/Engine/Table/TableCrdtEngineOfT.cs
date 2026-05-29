using OpStream.Constants;
using OpStream.Shared.Messages;
using System.Text.Json;

namespace OpStream.Server.Engine.Table;

/// <summary>
/// Strongly-typed facade over <see cref="TableCrdtEngine"/>: callers operate with a
/// domain <typeparamref name="TValue"/> for cell values (and optionally a typed column
/// definition through helper overloads) while the runtime engine still serializes
/// everything to <see cref="JsonElement"/>.
/// <para>
/// Mirrors the Awareness / Tree untyped-core + typed-facade pattern: there is a single
/// runtime engine, so the wire format and storage stay uniform across all clients.
/// </para>
/// </summary>
public sealed class TableCrdtEngine<TValue> : IOpEngine<TableDocument, TableOpBatch>
{
    private readonly TableCrdtEngine _inner = new();
    private readonly JsonSerializerOptions _jsonOptions;

    public TableCrdtEngine(JsonSerializerOptions? jsonOptions = null)
    {
        _jsonOptions = jsonOptions ?? OpStreamJsonOptions.Default;
    }

    public TableDocument Apply(TableDocument state, TableOpBatch op) => _inner.Apply(state, op);
    public TableOpBatch? Transform(TableOpBatch incoming, TableOpBatch existing, TransformPriority priority) => _inner.Transform(incoming, existing, priority);
    public TableOpBatch? Compose(TableOpBatch a, TableOpBatch b) => _inner.Compose(a, b);
    public TableOpBatch Invert(TableOpBatch op, TableDocument preState) => _inner.Invert(op, preState);
    public bool IsNoOp(TableOpBatch op) => _inner.IsNoOp(op);
    public TableOpBatch RestampToWin(TableOpBatch op, TableDocument currentState) => _inner.RestampToWin(op, currentState);

    /// <summary>Builds a <see cref="SetCellOp"/> with the typed value serialized.</summary>
    public SetCellOp BuildSetCell(string rowId, string columnId, TValue value, long timestamp, string peerId)
    {
        var element = JsonSerializer.SerializeToElement(value, _jsonOptions);
        return new SetCellOp(rowId, columnId, element, timestamp, peerId);
    }

    /// <summary>Reads a cell value back into <typeparamref name="TValue"/>.</summary>
    public TValue? ReadCell(CellRegister register) => register.Value.Deserialize<TValue>(_jsonOptions);
}
