using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpStream.Server.Engine.Table;

// ── State ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Metadata for a single row. Soft-deletion via <see cref="IsDeleted"/> is mandatory:
/// hard-removing a row would lose the causal context needed to resolve concurrent
/// edits ("user A wrote a cell while user B deleted the row").
/// </summary>
/// <param name="Id">Globally unique row id (peer-generated).</param>
/// <param name="Position">Lexicographic ordering key (see <see cref="Common.FractionalIndex"/>).</param>
/// <param name="PositionTimestamp">Logical clock for the position field (LWW).</param>
/// <param name="PositionPeerId">Tie-breaker for <paramref name="PositionTimestamp"/>.</param>
/// <param name="IsDeleted">True if a Remove op has been applied; sticky unless a Restore wins by LWW.</param>
/// <param name="DeletionTimestamp">Logical clock for the deletion/restoration LWW; 0 if never touched.</param>
/// <param name="DeletionPeerId">Tie-breaker for <paramref name="DeletionTimestamp"/>.</param>
public record RowMeta(
    string Id,
    string Position,
    long PositionTimestamp,
    string PositionPeerId,
    bool IsDeleted,
    long DeletionTimestamp,
    string DeletionPeerId);

/// <summary>
/// Metadata for a single column. Symmetric to <see cref="RowMeta"/> with an extra
/// <paramref name="Definition"/> blob (name, type, validators…) treated as a LWW register
/// keyed by <paramref name="DefinitionTimestamp"/>.
/// </summary>
public record ColumnMeta(
    string Id,
    string Position,
    long PositionTimestamp,
    string PositionPeerId,
    JsonElement Definition,
    long DefinitionTimestamp,
    string DefinitionPeerId,
    bool IsDeleted,
    long DeletionTimestamp,
    string DeletionPeerId);

/// <summary>
/// One cell's value plus the LWW metadata that owns it. <see cref="Value"/>'s
/// <see cref="JsonElement.ValueKind"/> = <see cref="JsonValueKind.Null"/> represents
/// a cleared cell (tombstoned register), distinguishable from an absent cell which
/// simply has no entry in <see cref="TableDocument.Cells"/>.
/// </summary>
public record CellRegister(JsonElement Value, long Timestamp, string PeerId);

/// <summary>
/// Stable, structural address of a cell.
/// </summary>
public readonly record struct CellAddress(string RowId, string ColumnId);

/// <summary>
/// Full table state. Tombstoned rows/columns remain in the dictionaries; consumers
/// filter them out at read-time. This is what allows Set on a row that one peer
/// concurrently deleted to remain part of the log without diverging replicas.
/// </summary>
public record TableDocument(
    IReadOnlyDictionary<string, RowMeta> Rows,
    IReadOnlyDictionary<string, ColumnMeta> Columns,
    IReadOnlyDictionary<CellAddress, CellRegister> Cells)
{
    public TableDocument() : this(
        new Dictionary<string, RowMeta>(),
        new Dictionary<string, ColumnMeta>(),
        new Dictionary<CellAddress, CellRegister>()) { }
}

// ── Operations ───────────────────────────────────────────────────────────────────────

/// <summary>
/// Base type for every table operation. Each variant carries its own
/// <c>Timestamp</c> + <c>PeerId</c> pair so the LWW resolution inside Apply is uniform.
/// </summary>
[JsonDerivedType(typeof(InsertRowOp), "ins_row")]
[JsonDerivedType(typeof(RemoveRowOp), "rm_row")]
[JsonDerivedType(typeof(RestoreRowOp), "rs_row")]
[JsonDerivedType(typeof(MoveRowOp), "mv_row")]
[JsonDerivedType(typeof(InsertColumnOp), "ins_col")]
[JsonDerivedType(typeof(RemoveColumnOp), "rm_col")]
[JsonDerivedType(typeof(RestoreColumnOp), "rs_col")]
[JsonDerivedType(typeof(MoveColumnOp), "mv_col")]
[JsonDerivedType(typeof(UpdateColumnDefinitionOp), "upd_col_def")]
[JsonDerivedType(typeof(SetCellOp), "set_cell")]
[JsonDerivedType(typeof(ClearCellOp), "clr_cell")]
public abstract record TableOp(long Timestamp, string PeerId);

// Rows
public record InsertRowOp(string RowId, string Position, long Timestamp, string PeerId) : TableOp(Timestamp, PeerId);
public record RemoveRowOp(string RowId, long Timestamp, string PeerId) : TableOp(Timestamp, PeerId);
public record RestoreRowOp(string RowId, long Timestamp, string PeerId) : TableOp(Timestamp, PeerId);
public record MoveRowOp(string RowId, string NewPosition, long Timestamp, string PeerId) : TableOp(Timestamp, PeerId);

// Columns
public record InsertColumnOp(string ColumnId, string Position, JsonElement Definition, long Timestamp, string PeerId) : TableOp(Timestamp, PeerId);
public record RemoveColumnOp(string ColumnId, long Timestamp, string PeerId) : TableOp(Timestamp, PeerId);
public record RestoreColumnOp(string ColumnId, long Timestamp, string PeerId) : TableOp(Timestamp, PeerId);
public record MoveColumnOp(string ColumnId, string NewPosition, long Timestamp, string PeerId) : TableOp(Timestamp, PeerId);
public record UpdateColumnDefinitionOp(string ColumnId, JsonElement Definition, long Timestamp, string PeerId) : TableOp(Timestamp, PeerId);

// Cells
public record SetCellOp(string RowId, string ColumnId, JsonElement Value, long Timestamp, string PeerId) : TableOp(Timestamp, PeerId);
public record ClearCellOp(string RowId, string ColumnId, long Timestamp, string PeerId) : TableOp(Timestamp, PeerId);

/// <summary>
/// Bundle of table ops applied atomically — same shape as <c>JsonOpBatch</c> /
/// <c>TreeOpBatch</c> so transport code stays uniform across engines.
/// </summary>
public record TableOpBatch(IReadOnlyList<TableOp> Operations)
{
    public static TableOpBatch Create(params TableOp[] ops) => new(ops);
}
