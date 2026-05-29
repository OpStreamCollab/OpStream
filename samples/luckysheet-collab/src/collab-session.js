import * as signalR from '@microsoft/signalr';

// Collaborative Luckysheet over OpStream's JSON CRDT engine.
//
// Each cell is a register at path `cells.<row>_<col>`. A local edit (the
// `cellUpdated` hook) becomes a JSON `set`/`del` op; a remote op is applied with
// `luckysheet.setCellValue`. Mirrors the structure of the three.js sample —
// only the editor-specific glue (capture + apply) differs.

const PROTOCOL_VERSION = 1;
const DOCUMENT_TYPE = 'json';
const PATH_PREFIX = 'cells.';

const b64ToUtf8 = (b64) => new TextDecoder().decode(
    Uint8Array.from(atob(b64), (c) => c.charCodeAt(0))
);
const utf8ToB64 = (str) => {
    const arr = new TextEncoder().encode(str);
    let bin = '';
    for (let i = 0; i < arr.length; i++) bin += String.fromCharCode(arr[i]);
    return btoa(bin);
};
// SignalR's JSON hub protocol expects byte[] params as a base64 string.
const opToPayload = (obj) => utf8ToB64(JSON.stringify(obj));

const randomPeerId = () => 'peer-' + Math.random().toString(36).slice(2, 10);

// cellUpdated hands us the new cell (object {v,m,...}, primitive, or null).
const cellPrimitive = (cell) => {
    if (cell == null) return '';
    if (typeof cell === 'object') return cell.v ?? cell.m ?? '';
    return cell;
};

export class CollabSession {
    constructor({ url, documentId, luckysheet, onStatus, onPeerId }) {
        this.url = url;
        this.documentId = documentId;
        this.luckysheet = luckysheet;
        this.onStatus = onStatus || (() => {});
        this.onPeerId = onPeerId || (() => {});

        this.peerId = randomPeerId();
        this.revision = 0;

        // Per-path latest-wins outbox: rapid edits to the same cell collapse to
        // the last value while a SendOp is in flight (LWW-safe under the Json engine).
        this.pending = new Map();
        this.flushing = false;

        // Echo suppression. setCellValue (used to apply remote ops) also fires the
        // cellUpdated hook; we record the value we just wrote per path and skip the
        // matching local callback. Value-based so it's robust to hook timing.
        this.suppress = new Map();

        this.connection = null;
    }

    async connect() {
        this.onStatus('connecting');
        this.onPeerId(this.peerId);

        this.connection = new signalR.HubConnectionBuilder()
            .withUrl(this.url)
            .withAutomaticReconnect()
            .build();

        this.connection.on('ReceiveOp', (payload, revision) => {
            this.revision = revision;
            this._applyRemoteBatch(JSON.parse(b64ToUtf8(payload)));
        });

        await this.connection.start();

        const joinResult = await this.connection.invoke(
            'JoinDocument', this.documentId, DOCUMENT_TYPE, PROTOCOL_VERSION,
        );
        this.revision = joinResult.revision;
        this._loadSnapshot(joinResult.snapshot);

        for (const pendingPayload of joinResult.pendingOps || []) {
            this._applyRemoteBatch(JSON.parse(b64ToUtf8(pendingPayload)));
        }

        this.onStatus('online');
    }

    // ── Local edit → op (called from the cellUpdated hook) ────────────────────
    onLocalEdit(row, col, newValue) {
        const path = `${PATH_PREFIX}${row}_${col}`;
        const value = cellPrimitive(newValue);

        // Skip the echo of a value we just applied from a remote op.
        if (this.suppress.has(path) && this.suppress.get(path) === value) {
            this.suppress.delete(path);
            return;
        }

        const ts = Date.now();
        const op = (value === '' || value == null)
            ? { $type: 'del', path, timestamp: ts, peerId: this.peerId }
            : { $type: 'set', path, value, timestamp: ts, peerId: this.peerId };

        this.pending.set(path, op);
        this._flush();
    }

    async _flush() {
        if (this.flushing) return;
        this.flushing = true;
        try {
            while (this.pending.size > 0) {
                const batch = { operations: Array.from(this.pending.values()) };
                this.pending.clear();
                try {
                    const result = await this.connection.invoke(
                        'SendOp', this.documentId, opToPayload(batch), this.revision,
                    );
                    if (result && result.success) this.revision = result.newRevision;
                } catch (err) {
                    console.error('[CollabSession] SendOp failed:', err);
                    break;
                }
            }
        } finally {
            this.flushing = false;
        }
    }

    // ── Snapshot / remote ops → sheet ─────────────────────────────────────────
    _loadSnapshot(snapshotB64) {
        if (!snapshotB64) return;
        const doc = JSON.parse(b64ToUtf8(snapshotB64));
        const registers = doc.registers || {};
        for (const [path, reg] of Object.entries(registers)) {
            if (reg.isDeleted) continue;
            this._applyPath(path, reg.value, /*isDelete*/ false);
        }
    }

    _applyRemoteBatch(opBatch) {
        for (const op of opBatch.operations || []) {
            this._applyPath(op.path, op.value, op.$type === 'del');
        }
    }

    _applyPath(path, value, isDelete) {
        if (!path.startsWith(PATH_PREFIX)) return;
        const [rowStr, colStr] = path.slice(PATH_PREFIX.length).split('_');
        const row = Number(rowStr);
        const col = Number(colStr);
        if (!Number.isInteger(row) || !Number.isInteger(col)) return;

        const next = isDelete ? '' : value;
        this.suppress.set(path, next);   // swallow the cellUpdated echo
        this.luckysheet.setCellValue(row, col, next, { isRefresh: true });
    }
}
