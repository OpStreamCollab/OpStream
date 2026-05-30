import { OpStreamSession } from 'opstream-collab';

// Luckysheet adapter for OpStream. Transport/outbox/snapshot live in
// `opstream-collab`; here we only map cells ⇄ ops.
//
// Each cell is a register at `cells.<row>_<col>`. A local edit (the cellUpdated
// hook) becomes a set/del; a remote op is applied with luckysheet.setCellValue.

const PREFIX = 'cells.';

// cellUpdated hands us the new cell (object {v,m,...}, primitive, or null).
const cellPrimitive = (cell) => {
    if (cell == null) return '';
    if (typeof cell === 'object') return cell.v ?? cell.m ?? '';
    return cell;
};

export class CollabSession {
    constructor({ url, documentId, luckysheet, onStatus }) {
        this.luckysheet = luckysheet;

        // Echo suppression: setCellValue (used to apply remote ops) also fires the
        // cellUpdated hook; record the value we just wrote per path and skip the
        // matching local callback. Value-based so it's robust to hook timing.
        this.suppress = new Map();

        this.session = new OpStreamSession({
            url, documentId,
            onStatus,
            applyOps: (ops) => { for (const op of ops) this._applyPath(op.path, op.value, op.isDelete); },
        });
    }

    connect() { return this.session.connect(); }

    // ── Local edit → op (called from the cellUpdated hook) ──────────────────────
    onLocalEdit(row, col, newValue) {
        const path = `${PREFIX}${row}_${col}`;
        const value = cellPrimitive(newValue);

        // Skip the echo of a value we just applied from a remote op.
        if (this.suppress.has(path) && this.suppress.get(path) === value) {
            this.suppress.delete(path);
            return;
        }

        if (value === '' || value == null) this.session.delPath(path);
        else this.session.setPath(path, value);
    }

    // ── Apply ─────────────────────────────────────────────────────────────────────
    _applyPath(path, value, isDelete) {
        if (!path.startsWith(PREFIX)) return;
        const [rowStr, colStr] = path.slice(PREFIX.length).split('_');
        const row = Number(rowStr);
        const col = Number(colStr);
        if (!Number.isInteger(row) || !Number.isInteger(col)) return;

        const next = isDelete ? '' : value;
        this.suppress.set(path, next); // swallow the cellUpdated echo
        this.luckysheet.setCellValue(row, col, next, { isRefresh: true });
    }
}
