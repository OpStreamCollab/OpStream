import * as signalR from '@microsoft/signalr';

// Collaborative 2D canvas over OpStream's JSON CRDT engine, on Fabric.js (v5).
//
// Each canvas object is a register at `objects.<id>` holding obj.toObject(['id']).
// Adding / modifying (move, scale, rotate) / removing an object becomes a JSON
// set/del op; remote ops are re-enlivened back onto the canvas. Same shape as the
// three.js sample — objects keyed by a stable id we assign on creation.
//
// Targets Fabric v5 (callback-style fabric.util.enlivenObjects). Fabric v6
// returns a Promise from enlivenObjects — see README.

const PROTOCOL_VERSION = 1;
const DOCUMENT_TYPE = 'json';
const PREFIX = 'objects.';

const b64ToUtf8 = (b64) => new TextDecoder().decode(
    Uint8Array.from(atob(b64), (c) => c.charCodeAt(0))
);
const utf8ToB64 = (str) => {
    const arr = new TextEncoder().encode(str);
    let bin = '';
    for (let i = 0; i < arr.length; i++) bin += String.fromCharCode(arr[i]);
    return btoa(bin);
};
const opToPayload = (obj) => utf8ToB64(JSON.stringify(obj));
const randomPeerId = () => 'peer-' + Math.random().toString(36).slice(2, 10);
const uuid = () => 'o-' + Math.random().toString(36).slice(2, 10);

export class CollabSession {
    constructor({ url, documentId, canvas, fabric, onStatus, onPeerId }) {
        this.url = url;
        this.documentId = documentId;
        this.canvas = canvas;
        this.fabric = fabric;
        this.onStatus = onStatus || (() => {});
        this.onPeerId = onPeerId || (() => {});

        this.peerId = randomPeerId();
        this.revision = 0;

        this.remoteApplyDepth = 0;
        this.pending = new Map();
        this.flushing = false;

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

        this._installHooks();
        this.onStatus('online');
    }

    // ── Capture local edits ───────────────────────────────────────────────────
    _installHooks() {
        const self = this;
        this.canvas.on('object:added', (e) => self._onAddedOrModified(e.target));
        this.canvas.on('object:modified', (e) => self._onAddedOrModified(e.target));
        this.canvas.on('object:removed', (e) => self._onRemoved(e.target));
    }

    _onAddedOrModified(obj) {
        if (!obj) return;
        if (!obj.id) obj.id = uuid();
        if (this.remoteApplyDepth > 0) return;
        this._enqueue({
            $type: 'set', path: PREFIX + obj.id, value: obj.toObject(['id']),
            timestamp: Date.now(), peerId: this.peerId,
        });
    }

    _onRemoved(obj) {
        if (!obj || !obj.id || this.remoteApplyDepth > 0) return;
        this._enqueue({
            $type: 'del', path: PREFIX + obj.id,
            timestamp: Date.now(), peerId: this.peerId,
        });
    }

    _enqueue(op) {
        this.pending.set(op.path, op);
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

    // ── Apply remote / snapshot ───────────────────────────────────────────────
    _loadSnapshot(snapshotB64) {
        if (!snapshotB64) return;
        const doc = JSON.parse(b64ToUtf8(snapshotB64));
        const registers = doc.registers || {};
        for (const [path, reg] of Object.entries(registers)) {
            if (reg.isDeleted) continue;
            this._applyPath(path, reg.value, false);
        }
    }

    _applyRemoteBatch(opBatch) {
        for (const op of opBatch.operations || []) {
            this._applyPath(op.path, op.value, op.$type === 'del');
        }
    }

    _findById(id) {
        return this.canvas.getObjects().find((o) => o.id === id) || null;
    }

    _applyPath(path, value, isDelete) {
        if (!path.startsWith(PREFIX)) return;
        const id = path.slice(PREFIX.length);

        this.remoteApplyDepth++;
        try {
            const existing = this._findById(id);
            if (existing) this.canvas.remove(existing);
            if (isDelete) { this.canvas.requestRenderAll(); return; }

            // Fabric v5: callback-style enliven.
            this.fabric.util.enlivenObjects([value], (objs) => {
                const o = objs[0];
                if (!o) return;
                o.id = id;
                this.remoteApplyDepth++;
                try { this.canvas.add(o); } finally {
                    Promise.resolve().then(() => { this.remoteApplyDepth--; });
                }
                this.canvas.requestRenderAll();
            });
        } finally {
            Promise.resolve().then(() => { this.remoteApplyDepth--; });
        }
    }
}
