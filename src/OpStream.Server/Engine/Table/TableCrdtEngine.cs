using OpStream.Server.Models;
using System.Text.Json;

namespace OpStream.Server.Engine.Table;

/// <summary>
/// CRDT engine for tabular data (spreadsheets, Airtable-style bases, grid views).
/// <para>
/// Strategy is LWW (last-writer-wins) on every mutable field with sticky tombstones
/// for row / column deletion — concurrency between a cell edit and a row/column delete
/// is preserved (cell value remains in the store but is filtered out at read-time).
/// All ties on <c>Timestamp</c> are broken by <c>PeerId</c> with ordinal string compare
/// so every replica converges to the same state regardless of delivery order.
/// </para>
/// <para>
/// Operations on rows / columns that haven't been observed yet are accepted: this keeps
/// causality flexible (a late-arriving InsertRow will find its cells already populated)
/// and matches the JsonCrdt convention.
/// </para>
/// </summary>
public class TableCrdtEngine : IOpEngine<TableDocument, TableOpBatch>
{
    public TableDocument Apply(TableDocument state, TableOpBatch batch)
    {
        var rows = new Dictionary<string, RowMeta>(state.Rows);
        var cols = new Dictionary<string, ColumnMeta>(state.Columns);
        var cells = new Dictionary<CellAddress, CellRegister>(state.Cells);

        foreach (var op in batch.Operations)
        {
            switch (op)
            {
                case InsertRowOp ins: ApplyInsertRow(rows, ins); break;
                case RemoveRowOp rm: ApplyRowTombstone(rows, rm.RowId, isDeleted: true, rm.Timestamp, rm.PeerId); break;
                case RestoreRowOp rs: ApplyRowTombstone(rows, rs.RowId, isDeleted: false, rs.Timestamp, rs.PeerId); break;
                case MoveRowOp mv: ApplyMoveRow(rows, mv); break;
                case InsertColumnOp ins: ApplyInsertColumn(cols, ins); break;
                case RemoveColumnOp rm: ApplyColumnTombstone(cols, rm.ColumnId, isDeleted: true, rm.Timestamp, rm.PeerId); break;
                case RestoreColumnOp rs: ApplyColumnTombstone(cols, rs.ColumnId, isDeleted: false, rs.Timestamp, rs.PeerId); break;
                case MoveColumnOp mv: ApplyMoveColumn(cols, mv); break;
                case UpdateColumnDefinitionOp upd: ApplyColumnDefinition(cols, upd); break;
                case SetCellOp set: ApplyCell(cells, set.RowId, set.ColumnId, set.Value, set.Timestamp, set.PeerId); break;
                case ClearCellOp clr: ApplyCell(cells, clr.RowId, clr.ColumnId, JsonNull(), clr.Timestamp, clr.PeerId); break;
                default: throw new NotSupportedException($"Unsupported table op: {op.GetType().Name}");
            }
        }

        return new TableDocument(rows, cols, cells);
    }

    /// <summary>
    /// CRDT: operations on different addresses commute, and same-address conflicts are
    /// settled by LWW inside Apply. There is no need to rebase at the transport layer.
    /// </summary>
    public TableOpBatch? Transform(TableOpBatch incoming, TableOpBatch existing, TransformPriority priority) => incoming;

    /// <summary>
    /// Compose is intentionally unsupported: collapsing two ops into one would lose the
    /// per-op LWW timestamps that other replicas need to converge.
    /// </summary>
    public TableOpBatch? Compose(TableOpBatch a, TableOpBatch b) => null;

    public TableOpBatch Invert(TableOpBatch batch, TableDocument preState)
    {
        var inverted = new List<TableOp>(batch.Operations.Count);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Walk in reverse so a batch that touches the same address twice undoes in the
        // correct order on Undo.
        for (int i = batch.Operations.Count - 1; i >= 0; i--)
        {
            var op = batch.Operations[i];
            long safeTs = Math.Max(now, op.Timestamp + 1);
            inverted.Add(InvertOne(op, preState, safeTs));
        }
        return new TableOpBatch(inverted);
    }

    public bool IsNoOp(TableOpBatch op) => op.Operations.Count == 0;

    /// <summary>
    /// Rewrites every op's <c>Timestamp</c> to a value strictly greater than every LWW
    /// timestamp currently present in <paramref name="currentState"/>.
    /// Used by <c>UndoRedoEngine</c> so a cached inverse, whose timestamps were assigned
    /// at record time, still wins LWW after concurrent writes have landed.
    /// </summary>
    public TableOpBatch RestampToWin(TableOpBatch op, TableDocument currentState)
    {
        if (op.Operations.Count == 0) return op;

        long max = 0;
        foreach (var r in currentState.Rows.Values)
        {
            if (r.PositionTimestamp > max) max = r.PositionTimestamp;
            if (r.DeletionTimestamp > max) max = r.DeletionTimestamp;
        }
        foreach (var c in currentState.Columns.Values)
        {
            if (c.PositionTimestamp > max) max = c.PositionTimestamp;
            if (c.DefinitionTimestamp > max) max = c.DefinitionTimestamp;
            if (c.DeletionTimestamp > max) max = c.DeletionTimestamp;
        }
        foreach (var cell in currentState.Cells.Values)
        {
            if (cell.Timestamp > max) max = cell.Timestamp;
        }
        long newTs = Math.Max(max, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) + 1;

        var rewritten = new List<TableOp>(op.Operations.Count);
        foreach (var top in op.Operations)
        {
            rewritten.Add(top switch
            {
                InsertRowOp x => x with { Timestamp = newTs },
                RemoveRowOp x => x with { Timestamp = newTs },
                RestoreRowOp x => x with { Timestamp = newTs },
                MoveRowOp x => x with { Timestamp = newTs },
                InsertColumnOp x => x with { Timestamp = newTs },
                RemoveColumnOp x => x with { Timestamp = newTs },
                RestoreColumnOp x => x with { Timestamp = newTs },
                MoveColumnOp x => x with { Timestamp = newTs },
                UpdateColumnDefinitionOp x => x with { Timestamp = newTs },
                SetCellOp x => x with { Timestamp = newTs },
                ClearCellOp x => x with { Timestamp = newTs },
                _ => top
            });
        }
        return new TableOpBatch(rewritten);
    }

    // ── Per-op apply ─────────────────────────────────────────────────────────────────

    private static void ApplyInsertRow(Dictionary<string, RowMeta> rows, InsertRowOp op)
    {
        if (rows.TryGetValue(op.RowId, out var existing))
        {
            // Re-insertion converges to the LWW winner for the position field;
            // tombstone state is left untouched.
            if (LwwWins(op.Timestamp, op.PeerId, existing.PositionTimestamp, existing.PositionPeerId))
            {
                rows[op.RowId] = existing with
                {
                    Position = op.Position,
                    PositionTimestamp = op.Timestamp,
                    PositionPeerId = op.PeerId
                };
            }
            return;
        }
        rows[op.RowId] = new RowMeta(
            Id: op.RowId,
            Position: op.Position,
            PositionTimestamp: op.Timestamp,
            PositionPeerId: op.PeerId,
            IsDeleted: false,
            DeletionTimestamp: 0,
            DeletionPeerId: string.Empty);
    }

    private static void ApplyRowTombstone(Dictionary<string, RowMeta> rows, string rowId, bool isDeleted, long ts, string peerId)
    {
        if (rows.TryGetValue(rowId, out var existing))
        {
            if (!LwwWins(ts, peerId, existing.DeletionTimestamp, existing.DeletionPeerId)) return;
            rows[rowId] = existing with
            {
                IsDeleted = isDeleted,
                DeletionTimestamp = ts,
                DeletionPeerId = peerId
            };
            return;
        }
        // Op references a row we haven't seen yet — record it so a future InsertRowOp converges.
        rows[rowId] = new RowMeta(
            Id: rowId,
            Position: string.Empty,
            PositionTimestamp: 0,
            PositionPeerId: string.Empty,
            IsDeleted: isDeleted,
            DeletionTimestamp: ts,
            DeletionPeerId: peerId);
    }

    private static void ApplyMoveRow(Dictionary<string, RowMeta> rows, MoveRowOp op)
    {
        if (rows.TryGetValue(op.RowId, out var existing))
        {
            if (!LwwWins(op.Timestamp, op.PeerId, existing.PositionTimestamp, existing.PositionPeerId)) return;
            rows[op.RowId] = existing with
            {
                Position = op.NewPosition,
                PositionTimestamp = op.Timestamp,
                PositionPeerId = op.PeerId
            };
            return;
        }
        rows[op.RowId] = new RowMeta(op.RowId, op.NewPosition, op.Timestamp, op.PeerId,
            IsDeleted: false, DeletionTimestamp: 0, DeletionPeerId: string.Empty);
    }

    private static void ApplyInsertColumn(Dictionary<string, ColumnMeta> cols, InsertColumnOp op)
    {
        if (cols.TryGetValue(op.ColumnId, out var existing))
        {
            var nextPosTs = existing.PositionTimestamp;
            var nextPosPeer = existing.PositionPeerId;
            var nextPos = existing.Position;
            if (LwwWins(op.Timestamp, op.PeerId, existing.PositionTimestamp, existing.PositionPeerId))
            {
                nextPos = op.Position;
                nextPosTs = op.Timestamp;
                nextPosPeer = op.PeerId;
            }
            var nextDefTs = existing.DefinitionTimestamp;
            var nextDefPeer = existing.DefinitionPeerId;
            var nextDef = existing.Definition;
            if (LwwWins(op.Timestamp, op.PeerId, existing.DefinitionTimestamp, existing.DefinitionPeerId))
            {
                nextDef = op.Definition;
                nextDefTs = op.Timestamp;
                nextDefPeer = op.PeerId;
            }
            cols[op.ColumnId] = existing with
            {
                Position = nextPos,
                PositionTimestamp = nextPosTs,
                PositionPeerId = nextPosPeer,
                Definition = nextDef,
                DefinitionTimestamp = nextDefTs,
                DefinitionPeerId = nextDefPeer
            };
            return;
        }
        cols[op.ColumnId] = new ColumnMeta(
            Id: op.ColumnId,
            Position: op.Position,
            PositionTimestamp: op.Timestamp,
            PositionPeerId: op.PeerId,
            Definition: op.Definition,
            DefinitionTimestamp: op.Timestamp,
            DefinitionPeerId: op.PeerId,
            IsDeleted: false,
            DeletionTimestamp: 0,
            DeletionPeerId: string.Empty);
    }

    private static void ApplyColumnTombstone(Dictionary<string, ColumnMeta> cols, string columnId, bool isDeleted, long ts, string peerId)
    {
        if (cols.TryGetValue(columnId, out var existing))
        {
            if (!LwwWins(ts, peerId, existing.DeletionTimestamp, existing.DeletionPeerId)) return;
            cols[columnId] = existing with
            {
                IsDeleted = isDeleted,
                DeletionTimestamp = ts,
                DeletionPeerId = peerId
            };
            return;
        }
        cols[columnId] = new ColumnMeta(
            Id: columnId,
            Position: string.Empty,
            PositionTimestamp: 0,
            PositionPeerId: string.Empty,
            Definition: JsonNull(),
            DefinitionTimestamp: 0,
            DefinitionPeerId: string.Empty,
            IsDeleted: isDeleted,
            DeletionTimestamp: ts,
            DeletionPeerId: peerId);
    }

    private static void ApplyMoveColumn(Dictionary<string, ColumnMeta> cols, MoveColumnOp op)
    {
        if (cols.TryGetValue(op.ColumnId, out var existing))
        {
            if (!LwwWins(op.Timestamp, op.PeerId, existing.PositionTimestamp, existing.PositionPeerId)) return;
            cols[op.ColumnId] = existing with
            {
                Position = op.NewPosition,
                PositionTimestamp = op.Timestamp,
                PositionPeerId = op.PeerId
            };
            return;
        }
        cols[op.ColumnId] = new ColumnMeta(op.ColumnId, op.NewPosition, op.Timestamp, op.PeerId,
            Definition: JsonNull(), DefinitionTimestamp: 0, DefinitionPeerId: string.Empty,
            IsDeleted: false, DeletionTimestamp: 0, DeletionPeerId: string.Empty);
    }

    private static void ApplyColumnDefinition(Dictionary<string, ColumnMeta> cols, UpdateColumnDefinitionOp op)
    {
        if (cols.TryGetValue(op.ColumnId, out var existing))
        {
            if (!LwwWins(op.Timestamp, op.PeerId, existing.DefinitionTimestamp, existing.DefinitionPeerId)) return;
            cols[op.ColumnId] = existing with
            {
                Definition = op.Definition,
                DefinitionTimestamp = op.Timestamp,
                DefinitionPeerId = op.PeerId
            };
            return;
        }
        cols[op.ColumnId] = new ColumnMeta(op.ColumnId, string.Empty, 0, string.Empty,
            op.Definition, op.Timestamp, op.PeerId,
            IsDeleted: false, DeletionTimestamp: 0, DeletionPeerId: string.Empty);
    }

    private static void ApplyCell(Dictionary<CellAddress, CellRegister> cells, string rowId, string columnId, JsonElement value, long ts, string peerId)
    {
        var address = new CellAddress(rowId, columnId);
        if (cells.TryGetValue(address, out var existing))
        {
            if (!LwwWins(ts, peerId, existing.Timestamp, existing.PeerId)) return;
        }
        cells[address] = new CellRegister(value, ts, peerId);
    }

    // ── Invert ───────────────────────────────────────────────────────────────────────

    private static TableOp InvertOne(TableOp op, TableDocument preState, long safeTs)
    {
        switch (op)
        {
            case InsertRowOp ins:
                // Inverse of an insertion that newly created the row is a remove;
                // if the row already existed pre-op, undo by restoring its previous position.
                if (preState.Rows.TryGetValue(ins.RowId, out var prevRow))
                {
                    return new MoveRowOp(ins.RowId, prevRow.Position, safeTs, ins.PeerId);
                }
                return new RemoveRowOp(ins.RowId, safeTs, ins.PeerId);

            case RemoveRowOp rm:
                return new RestoreRowOp(rm.RowId, safeTs, rm.PeerId);

            case RestoreRowOp rs:
                return new RemoveRowOp(rs.RowId, safeTs, rs.PeerId);

            case MoveRowOp mv:
                if (preState.Rows.TryGetValue(mv.RowId, out var oldRow))
                    return new MoveRowOp(mv.RowId, oldRow.Position, safeTs, mv.PeerId);
                return new RemoveRowOp(mv.RowId, safeTs, mv.PeerId);

            case InsertColumnOp insC:
                if (preState.Columns.TryGetValue(insC.ColumnId, out var prevCol))
                {
                    // Restore previous position + definition with two ops would be cleaner; v1
                    // restores the position only (LWW on definition is rare in practice).
                    return new MoveColumnOp(insC.ColumnId, prevCol.Position, safeTs, insC.PeerId);
                }
                return new RemoveColumnOp(insC.ColumnId, safeTs, insC.PeerId);

            case RemoveColumnOp rmC:
                return new RestoreColumnOp(rmC.ColumnId, safeTs, rmC.PeerId);

            case RestoreColumnOp rsC:
                return new RemoveColumnOp(rsC.ColumnId, safeTs, rsC.PeerId);

            case MoveColumnOp mvC:
                if (preState.Columns.TryGetValue(mvC.ColumnId, out var oldCol))
                    return new MoveColumnOp(mvC.ColumnId, oldCol.Position, safeTs, mvC.PeerId);
                return new RemoveColumnOp(mvC.ColumnId, safeTs, mvC.PeerId);

            case UpdateColumnDefinitionOp updC:
                if (preState.Columns.TryGetValue(updC.ColumnId, out var defCol))
                    return new UpdateColumnDefinitionOp(updC.ColumnId, defCol.Definition, safeTs, updC.PeerId);
                return new UpdateColumnDefinitionOp(updC.ColumnId, JsonNull(), safeTs, updC.PeerId);

            case SetCellOp setOp:
                if (preState.Cells.TryGetValue(new CellAddress(setOp.RowId, setOp.ColumnId), out var prevCell))
                {
                    if (prevCell.Value.ValueKind == JsonValueKind.Null)
                        return new ClearCellOp(setOp.RowId, setOp.ColumnId, safeTs, setOp.PeerId);
                    return new SetCellOp(setOp.RowId, setOp.ColumnId, prevCell.Value, safeTs, setOp.PeerId);
                }
                return new ClearCellOp(setOp.RowId, setOp.ColumnId, safeTs, setOp.PeerId);

            case ClearCellOp clrOp:
                if (preState.Cells.TryGetValue(new CellAddress(clrOp.RowId, clrOp.ColumnId), out var prevForClear))
                {
                    if (prevForClear.Value.ValueKind == JsonValueKind.Null)
                        return new ClearCellOp(clrOp.RowId, clrOp.ColumnId, safeTs, clrOp.PeerId);
                    return new SetCellOp(clrOp.RowId, clrOp.ColumnId, prevForClear.Value, safeTs, clrOp.PeerId);
                }
                // No prior value — the inverse of clearing an already-empty cell is a no-op,
                // but Invert must return *some* op. Re-clearing is the safest neutral choice.
                return new ClearCellOp(clrOp.RowId, clrOp.ColumnId, safeTs, clrOp.PeerId);

            default:
                throw new NotSupportedException($"Cannot invert table op: {op.GetType().Name}");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if (incomingTs, incomingPeer) strictly wins LWW over (existingTs, existingPeer).
    /// Ties on timestamp are broken by ordinal peerId compare so all replicas agree.
    /// </summary>
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
