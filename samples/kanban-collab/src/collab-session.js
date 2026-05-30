import * as signalR from '@microsoft/signalr';

// Collaborative Kanban board over OpStream's JSON CRDT engine.
//
// Each card is a register at `cards.<id>` = { text, column, order }. Adding,
// editing, moving (drag between columns) or deleting a card becomes a JSON
// set/del op; remote ops update the local card map and trigger a re-render.
// Same shape as the three.js / Luckysheet samples — only the app glue differs.

const PROTOCOL_VERSION = 1;
const DOCUMENT_TYPE = 'json';
const PREFIX = 'cards.';

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

export class CollabSession {
    constructor({ url, documentId, onStatus, onRender, onPeerId }) {
        this.url = url;
        this.documentId = documentId;
        this.onStatus = onStatus || (() => {});
        this.onRender = onRender || (() => {});
        this.onPeerId = onPeerId || (() => {});

        this.peerId = randomPeerId();
        this.revision = 0;

        this.cards = new Map();   // id -> { text, column, order }
        this.pending = new Map(); // per-path latest-wins outbox
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
            this.onRender(this.cards);
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
        this.onRender(this.cards);
    }

    // ── Local mutations (called from the UI) ──────────────────────────────────
    upsertCard(id, card) {
        this.cards.set(id, card);
        this._enqueue({
            $type: 'set', path: PREFIX + id, value: card,
            timestamp: Date.now(), peerId: this.peerId,
        });
    }

    deleteCard(id) {
        this.cards.delete(id);
        this._enqueue({
            $type: 'del', path: PREFIX + id,
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

    _applyPath(path, value, isDelete) {
        if (!path.startsWith(PREFIX)) return;
        const id = path.slice(PREFIX.length);
        if (isDelete) this.cards.delete(id);
        else this.cards.set(id, value);
    }
}
