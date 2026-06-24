import { OpStreamSession } from 'opstream-collab';

// Fabric.js (v5) adapter for OpStream. Transport/outbox/snapshot/presence/comments
// live in `opstream-collab`; here we only map fabric objects ⇄ ops.
//
// Schema: one register per object at `objects.<id>` = obj.toObject(['id']).
// Capture: object:added / object:modified / object:removed.
// Apply: remove + re-create via fabric.util.enlivenObjects (v5 callback style).

const PREFIX = 'objects.';
const ANCHOR_KIND = 'fabric-object'; // object ids are stable → no anchor rebasing
const uuid = () => 'o-' + Math.random().toString(36).slice(2, 10);

export class CollabSession {
    constructor({ url, documentId, canvas, fabric, presence,
                  onStatus, onPeers, onRemoteEdit, onComments }) {
        this.canvas = canvas;
        this.fabric = fabric;
        this._onRemoteEdit = onRemoteEdit || (() => {});
        this.remoteApplyDepth = 0;

        this.session = new OpStreamSession({
            url, documentId,
            presence,
            comments: { kind: ANCHOR_KIND },
            onStatus,
            onPeers: (peers) => (onPeers || (() => {}))(
                peers.map((p) => ({ name: p.name, color: p.color }))),
            onComments: (list) => (onComments || (() => {}))(
                list.map((c) => ({
                    id: c.id,
                    body: c.body,
                    authorName: c.authorName || this.session.getPeerByConn(c.authorPeerId)?.name || 'Anonymous',
                    objectId: c.anchor && c.anchor.data ? (c.anchor.data.objectId ?? null) : null,
                }))),
            // Edit feedback is handled inside _applyOp (fabric's enliven is async, so
            // the lib's synchronous onRemoteEdit would fire before the node exists).
            applyOps: (ops, ctx) => { for (const op of ops) this._applyOp(op, ctx); },
        });
    }

    async connect() {
        await this.session.connect();
        this._installHooks();
    }

    peer(id) { return this.session.getPeer(id) || { name: 'Someone', color: '#9aa3ad' }; }
    addComment(objectId, body) { return this.session.addComment({ objectId }, body); }
    resolveComment(id) { return this.session.resolveComment(id); }

    // ── Capture ─────────────────────────────────────────────────────────────────
    _installHooks() {
        const self = this;
        this.canvas.on('object:added', (e) => self._onAddedOrModified(e.target));
        this.canvas.on('object:modified', (e) => self._onAddedOrModified(e.target));
        this.canvas.on('object:removed', (e) => self._onRemoved(e.target));
    }

    _syncable(obj) { return obj && !obj.excludeFromExport; }

    _onAddedOrModified(obj) {
        if (!this._syncable(obj)) return;
        if (!obj.id) obj.id = uuid();
        if (this.remoteApplyDepth > 0) return;
        this.session.setPath(PREFIX + obj.id, obj.toObject(['id']));
    }

    _onRemoved(obj) {
        if (!this._syncable(obj) || !obj.id || this.remoteApplyDepth > 0) return;
        this.session.delPath(PREFIX + obj.id);
    }

    // ── Apply ─────────────────────────────────────────────────────────────────────
    _findById(id) { return this.canvas.getObjects().find((o) => o.id === id) || null; }

    _applyOp(op, ctx) {
        if (!op.path.startsWith(PREFIX)) return;
        const id = op.path.slice(PREFIX.length);

        this.remoteApplyDepth++;
        try {
            const existing = this._findById(id);
            if (existing) this.canvas.remove(existing);
            if (op.isDelete) {
                this.canvas.requestRenderAll();
                if (!ctx.fromSnapshot && op.peerId) this._onRemoteEdit(id, this.peer(op.peerId), 'del');
                return;
            }
            this.fabric.util.enlivenObjects([op.value], (objs) => {
                const o = objs[0];
                if (!o) return;
                o.id = id;
                this.remoteApplyDepth++;
                try { this.canvas.add(o); } finally {
                    Promise.resolve().then(() => { this.remoteApplyDepth--; });
                }
                this.canvas.requestRenderAll();
                if (!ctx.fromSnapshot && op.peerId && op.peerId !== this.session.peerId) {
                    this._onRemoteEdit(id, this.peer(op.peerId), 'set');
                }
            });
        } finally {
            Promise.resolve().then(() => { this.remoteApplyDepth--; });
        }
    }

    // Used by main.js to read a comment's anchored object id.
    static objectIdOf(dto) { return dto ? (dto.objectId ?? null) : null; }
}
