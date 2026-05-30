import * as signalR from '@microsoft/signalr';

// Collaborative 2D canvas over OpStream's JSON CRDT engine, on Fabric.js (v5).
//
// Beyond syncing objects (objects.<id>), this session also carries:
//   • Awareness — each peer broadcasts { peerId, name, color } so remote edits
//     can be attributed to a named, colored author.
//   • Comments — anchored to an object via AnchorJson = {"objectId": "<id>"}.
//
// Targets Fabric v5 (callback-style fabric.util.enlivenObjects).

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
const uuid = () => 'o-' + Math.random().toString(36).slice(2, 10);

export class CollabSession {
    constructor({ url, documentId, canvas, fabric, presence,
                  onStatus, onPeers, onRemoteEdit, onComments }) {
        this.url = url;
        this.documentId = documentId;
        this.canvas = canvas;
        this.fabric = fabric;
        this.presence = presence || { peerId: 'peer-' + Math.random().toString(36).slice(2, 8),
                                      name: 'Anonymous', color: '#888' };
        this.peerId = this.presence.peerId;

        this.onStatus = onStatus || (() => {});
        this.onPeers = onPeers || (() => {});
        this.onRemoteEdit = onRemoteEdit || (() => {});   // (objectId, peer, kind)
        this.onComments = onComments || (() => {});        // (cards: CommentDto[])

        this.revision = 0;
        this.remoteApplyDepth = 0;
        this.pending = new Map();
        this.flushing = false;

        this.peers = new Map();      // peerId -> { name, color }
        this.comments = new Map();   // commentId -> CommentDto
        this.connection = null;
    }

    peer(id) { return this.peers.get(id) || { name: 'Someone', color: '#9aa3ad' }; }

    async connect() {
        this.onStatus('connecting');
        this.connection = new signalR.HubConnectionBuilder()
            .withUrl(this.url).withAutomaticReconnect().build();

        this.connection.on('ReceiveOp', (payload, revision) => {
            this.revision = revision;
            const batch = JSON.parse(b64ToUtf8(payload));
            console.log('[collab] RECV ops', (batch.operations || []).map((o) => o.path));
            this._applyRemoteBatch(batch);
        });
        this.connection.on('ReceiveAwareness', (data) => this._onAwareness(data));
        this.connection.on('PeerDisconnected', () => { /* connectionId-keyed; ignored */ });
        this.connection.on('ReceiveCommentCreated', (dto) => this._upsertComment(dto));
        this.connection.on('ReceiveCommentUpdated', (dto) => this._upsertComment(dto));
        this.connection.on('ReceiveCommentDeleted', (m) => this._removeComment(m && (m.commentId || m.CommentId || m)));

        await this.connection.start();
        const joinResult = await this.connection.invoke(
            'JoinDocument', this.documentId, DOCUMENT_TYPE, PROTOCOL_VERSION);
        this.revision = joinResult.revision;
        this._loadSnapshot(joinResult.snapshot);
        for (const p of joinResult.pendingOps || []) {
            this._applyRemoteBatch(JSON.parse(b64ToUtf8(p)));
        }

        this._installHooks();
        await this._broadcastPresence();
        await this._loadComments();
        this.onStatus('online');
    }

    // ── Object sync (capture) ─────────────────────────────────────────────────
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
        console.log('[collab] SEND set', obj.id, obj.type);
        this._enqueue({ $type: 'set', path: PREFIX + obj.id, value: obj.toObject(['id']),
            timestamp: Date.now(), peerId: this.peerId });
    }

    _onRemoved(obj) {
        if (!this._syncable(obj) || !obj.id || this.remoteApplyDepth > 0) return;
        this._enqueue({ $type: 'del', path: PREFIX + obj.id, timestamp: Date.now(), peerId: this.peerId });
    }

    _enqueue(op) { this.pending.set(op.path, op); this._flush(); }

    async _flush() {
        if (this.flushing) return;
        this.flushing = true;
        try {
            while (this.pending.size > 0) {
                const batch = { operations: Array.from(this.pending.values()) };
                this.pending.clear();
                try {
                    const r = await this.connection.invoke('SendOp', this.documentId, opToPayload(batch), this.revision);
                    if (r && r.success) this.revision = r.newRevision;
                } catch (err) { console.error('[CollabSession] SendOp failed:', err); break; }
            }
        } finally { this.flushing = false; }
    }

    // ── Object sync (apply) ───────────────────────────────────────────────────
    _loadSnapshot(snapshotB64) {
        if (!snapshotB64) return;
        const doc = JSON.parse(b64ToUtf8(snapshotB64));
        const regs = doc.registers || {};
        for (const [path, reg] of Object.entries(regs)) {
            if (reg.isDeleted) continue;
            this._applyPath(path, reg.value, false, null);
        }
    }

    _applyRemoteBatch(opBatch) {
        for (const op of opBatch.operations || []) {
            this._applyPath(op.path, op.value, op.$type === 'del', op.peerId);
        }
    }

    _findById(id) { return this.canvas.getObjects().find((o) => o.id === id) || null; }

    _applyPath(path, value, isDelete, byPeerId) {
        if (!path.startsWith(PREFIX)) return;
        const id = path.slice(PREFIX.length);

        this.remoteApplyDepth++;
        try {
            const existing = this._findById(id);
            if (existing) this.canvas.remove(existing);
            if (isDelete) { this.canvas.requestRenderAll(); if (byPeerId) this.onRemoteEdit(id, this.peer(byPeerId), 'del'); return; }

            this.fabric.util.enlivenObjects([value], (objs) => {
                const o = objs[0];
                if (!o) return;
                o.id = id;
                this.remoteApplyDepth++;
                try { this.canvas.add(o); } finally {
                    Promise.resolve().then(() => { this.remoteApplyDepth--; });
                }
                this.canvas.requestRenderAll();
                if (byPeerId) this.onRemoteEdit(id, this.peer(byPeerId), 'set');
            });
        } finally {
            Promise.resolve().then(() => { this.remoteApplyDepth--; });
        }
    }

    // ── Awareness / presence ──────────────────────────────────────────────────
    async _broadcastPresence() {
        try {
            await this.connection.invoke('UpdateAwareness', this.documentId, {
                peerId: this.peerId, name: this.presence.name, color: this.presence.color,
            });
        } catch (err) { console.warn('[CollabSession] presence failed', err); }
    }

    _onAwareness(payload) {
        const list = Array.isArray(payload) ? payload : [payload];
        for (const s of list) {
            // Server wraps as { peerId, data, lastUpdated } (camelCase); data is our payload.
            const d = (s && (s.data ?? s)) || {};
            if (d.peerId) this.peers.set(d.peerId, { name: d.name || 'Anonymous', color: d.color || '#888' });
        }
        this.onPeers([...this.peers.values()]);
    }

    // ── Comments ──────────────────────────────────────────────────────────────
    // Normalize the server `Comment` (SignalR casing varies) to a stable shape.
    _norm(c) {
        if (!c) return null;
        return {
            id: c.id ?? c.Id,
            body: c.body ?? c.Body,
            anchorJson: c.anchorJson ?? c.AnchorJson,
            authorId: c.authorId ?? c.AuthorId ?? '',
            isResolved: c.isResolved ?? c.IsResolved ?? false,
        };
    }

    async _loadComments() {
        try {
            const list = await this.connection.invoke('ListOpenComments', this.documentId);
            for (const c of list || []) this._upsertComment(c); // upsert filters resolved
        } catch (err) { console.warn('[CollabSession] ListOpenComments failed', err); }
    }

    async addComment(objectId, body) {
        // Hub: CreateComment(documentId, NewCommentCmd { Body, Anchor: CommentAnchor(Kind, Payload), ParentCommentId }).
        // Kind 'fabric-object' has no anchor engine — fine, since a fabric object's
        // id never changes, so the anchor never needs rebasing.
        const anchor = { kind: 'fabric-object', payload: { objectId } };
        const dto = await this.connection.invoke('CreateComment', this.documentId,
            { body, anchor, parentCommentId: null });
        this._upsertComment(dto);
        return dto;
    }

    async resolveComment(commentId) {
        await this.connection.invoke('ResolveComment', this.documentId, commentId);
        this._removeComment(commentId); // ResolveComment returns/echoes an updated (resolved) comment
    }

    // ListOpenComments returns every non-deleted comment (incl. resolved), and
    // ReceiveCommentUpdated fires for resolves too — so drop resolved ones here.
    _upsertComment(raw) {
        const dto = this._norm(raw);
        if (!dto || !dto.id) return;
        if (dto.isResolved) this.comments.delete(dto.id);
        else this.comments.set(dto.id, dto);
        this._emitComments();
    }
    _removeComment(id) { if (id && this.comments.delete(id)) this._emitComments(); }
    _emitComments() { this.onComments([...this.comments.values()]); }

    // objectId helper for the UI. AnchorJson may hold the whole CommentAnchor
    // ({kind,payload:{objectId}}) or just the payload — handle both/casing.
    static objectIdOf(dto) {
        try {
            const a = JSON.parse(dto.anchorJson || '{}');
            const p = a.payload ?? a.Payload ?? a;
            return p.objectId ?? p.ObjectId ?? null;
        } catch { return null; }
    }
}
