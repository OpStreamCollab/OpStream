import * as signalR from '@microsoft/signalr';

// Collaborative GoJS flowchart over OpStream's JSON CRDT engine.
//
// Sync: each node is a register at `nodes.<key>`, each link at `links.<key>`
// (string uuid keys, stable across peers). Capture: on each finished
// transaction we diff the model vs our baseline and emit set/del per changed
// key. Apply: model mutations inside a guarded commit.
//
// Beyond sync this session also carries:
//   • Awareness — each peer broadcasts { peerId, name, color }; remote edits are
//     attributed to a named, colored author (border flash + label on the node).
//   • Comments — anchored to a node by its key.
//
// Verified server wire contract (do NOT trust older docs):
//   - Awareness is pushed as the **ReceiveAwarenessUpdate** event, ONE
//     AwarenessState `{ peerId: <connId>, data: <our payload>, lastUpdated }`.
//     The server excludes the sender and only emits on change, so peers
//     re-broadcast when they learn of a newcomer.
//   - Comment DTO: `{ id, authorPeerId, body, anchor: { kind, data }, resolvedAt,
//     isOrphaned, ... }` — NO `anchorJson`, NO `isResolved`. Create with
//     `anchor: { kind, data }` (NOT `payload`). Resolved = `resolvedAt != null`.
//
// GoJS is commercial; the free evaluation build works identically but watermarks
// the canvas. See README.

const PROTOCOL_VERSION = 1;
const DOCUMENT_TYPE = 'json';
const N = 'nodes.';
const L = 'links.';
const ANCHOR_KIND = 'gojs-node'; // custom kind: node keys are stable → no rebasing needed

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
    constructor({ url, documentId, diagram, presence,
                  onStatus, onPeers, onRemoteEdit, onComments }) {
        this.url = url;
        this.documentId = documentId;
        this.diagram = diagram;
        this.model = diagram.model;

        this.presence = presence || { name: 'Anonymous', color: '#888' };
        this.peerId = randomPeerId();              // our id, carried in every op + awareness.data

        this.onStatus = onStatus || (() => {});
        this.onPeers = onPeers || (() => {});       // (peers: {name,color}[])
        this.onRemoteEdit = onRemoteEdit || (() => {}); // (nodeKey, {name,color}, kind)
        this.onComments = onComments || (() => {});  // (cards: CommentDto[])

        this.revision = 0;
        this.remoteApply = false;
        this.lastNodes = new Map(); // key -> JSON.stringify(nodeData)
        this.lastLinks = new Map();
        this.pending = new Map();
        this.flushing = false;

        this.peers = new Map();      // ourPeerId  -> { name, color, connId }
        this.peersByConn = new Map();// connId      -> { name, color }
        this.comments = new Map();   // commentId   -> normalized dto
        this.connection = null;
    }

    // Attribute a remote OP (op.peerId is the sender's random peerId).
    peerByOp(id) { return this.peers.get(id) || { name: 'Someone', color: '#9aa3ad' }; }
    // Attribute a comment (authorPeerId is the server ConnectionId).
    peerByConn(id) { return this.peersByConn.get(id) || null; }

    async connect() {
        this.onStatus('connecting');
        this.connection = new signalR.HubConnectionBuilder()
            .withUrl(this.url).withAutomaticReconnect().build();

        this.connection.on('ReceiveOp', (payload, revision) => {
            this.revision = revision;
            this._applyRemoteBatch(JSON.parse(b64ToUtf8(payload)));
        });
        // The server pushes ONE AwarenessState per "ReceiveAwarenessUpdate".
        this.connection.on('ReceiveAwarenessUpdate', (state) => this._onAwareness(state));
        this.connection.on('PeerDisconnected', (connId) => this._onPeerGone(connId));
        this.connection.on('ReceiveCommentCreated', (dto) => this._upsertComment(dto));
        this.connection.on('ReceiveCommentUpdated', (dto) => this._upsertComment(dto));
        this.connection.on('ReceiveCommentDeleted', (m) =>
            this._removeComment(m && (m.commentId ?? m.CommentId ?? m)));

        await this.connection.start();
        // We never receive our OWN awareness (server excludes the sender), so seed
        // our connId→name mapping here — otherwise our own comments would show the
        // raw ConnectionId instead of our name.
        if (this.connection.connectionId) {
            this.peersByConn.set(this.connection.connectionId,
                { name: this.presence.name, color: this.presence.color });
        }
        const joinResult = await this.connection.invoke(
            'JoinDocument', this.documentId, DOCUMENT_TYPE, PROTOCOL_VERSION);
        this.revision = joinResult.revision;
        this._loadSnapshot(joinResult.snapshot);
        for (const p of joinResult.pendingOps || []) {
            this._applyRemoteBatch(JSON.parse(b64ToUtf8(p)));
        }

        this._snapshotBaseline();
        this.diagram.addModelChangedListener((e) => {
            if (e.isTransactionFinished && !this.remoteApply) this._captureChanges();
        });

        await this._broadcastPresence();
        await this._loadComments();
        // Heartbeat: keeps "who's here" converged even with reconnects.
        this._hb = setInterval(() => this._broadcastPresence(), 8000);
        this.onStatus('online');
    }

    // ── Sync: capture (diff vs baseline) ──────────────────────────────────────
    _nodeKey(d) { return this.model.getKeyForNodeData(d); }
    _linkKey(d) { return d[this.model.linkKeyProperty]; }

    _snapshotBaseline() {
        this.lastNodes.clear();
        this.lastLinks.clear();
        for (const d of this.model.nodeDataArray) this.lastNodes.set(this._nodeKey(d), JSON.stringify(d));
        for (const d of this.model.linkDataArray) this.lastLinks.set(this._linkKey(d), JSON.stringify(d));
    }

    _captureChanges() {
        this._diff(this.model.nodeDataArray, this.lastNodes, this._nodeKey.bind(this), N);
        this._diff(this.model.linkDataArray, this.lastLinks, this._linkKey.bind(this), L);
    }

    _diff(arr, last, keyOf, prefix) {
        const seen = new Set();
        for (const d of arr) {
            const key = keyOf(d);
            if (key == null) continue;
            seen.add(key);
            const js = JSON.stringify(d);
            if (last.get(key) !== js) {
                last.set(key, js);
                this._enqueue({ $type: 'set', path: prefix + key, value: JSON.parse(js),
                    timestamp: Date.now(), peerId: this.peerId });
            }
        }
        for (const key of [...last.keys()]) {
            if (!seen.has(key)) {
                last.delete(key);
                this._enqueue({ $type: 'del', path: prefix + key,
                    timestamp: Date.now(), peerId: this.peerId });
            }
        }
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
                    const r = await this.connection.invoke(
                        'SendOp', this.documentId, opToPayload(batch), this.revision);
                    if (r && r.success) this.revision = r.newRevision;
                } catch (err) { console.error('[CollabSession] SendOp failed:', err); break; }
            }
        } finally { this.flushing = false; }
    }

    // ── Sync: apply remote / snapshot ─────────────────────────────────────────
    _loadSnapshot(snapshotB64) {
        if (!snapshotB64) return;
        const doc = JSON.parse(b64ToUtf8(snapshotB64));
        const regs = doc.registers || {};
        const ops = Object.entries(regs)
            .filter(([, r]) => !r.isDeleted)
            .map(([path, r]) => ({ path, value: r.value, del: false, peerId: null }));
        this._applyOrdered(ops, /*fromSnapshot*/ true);
    }

    _applyRemoteBatch(opBatch) {
        const ops = (opBatch.operations || []).map((o) => ({
            path: o.path, value: o.value, del: o.$type === 'del', peerId: o.peerId }));
        this._applyOrdered(ops, false);
    }

    // Nodes before links (a link needs both endpoints to exist).
    _applyOrdered(ops, fromSnapshot) {
        const nodeOps = ops.filter((o) => o.path.startsWith(N));
        const linkOps = ops.filter((o) => o.path.startsWith(L));
        if (!nodeOps.length && !linkOps.length) return;
        this.remoteApply = true;
        try {
            // Model.commit passes the Model (Diagram.commit would pass the Diagram).
            this.diagram.model.commit((m) => {
                for (const o of nodeOps) this._applyNode(m, o.path.slice(N.length), o.value, o.del);
                for (const o of linkOps) this._applyLink(m, o.path.slice(L.length), o.value, o.del);
            }, 'remote');
        } finally {
            this.remoteApply = false;
            this._snapshotBaseline(); // adopt applied state so it isn't re-sent
        }
        // Edit feedback (only for live remote ops, never snapshot replay).
        if (!fromSnapshot) {
            for (const o of nodeOps) {
                if (!o.peerId || o.peerId === this.peerId) continue;
                this.onRemoteEdit(o.path.slice(N.length), this.peerByOp(o.peerId), o.del ? 'del' : 'set');
            }
        }
    }

    _applyNode(m, key, value, del) {
        const existing = m.findNodeDataForKey(key);
        if (del) { if (existing) m.removeNodeData(existing); return; }
        if (existing) { for (const k of Object.keys(value)) m.setDataProperty(existing, k, value[k]); }
        else m.addNodeData(value);
    }

    _applyLink(m, key, value, del) {
        const existing = m.findLinkDataForKey ? m.findLinkDataForKey(key) : null;
        if (del) { if (existing) m.removeLinkData(existing); return; }
        if (existing) { for (const k of Object.keys(value)) m.setDataProperty(existing, k, value[k]); }
        else m.addLinkData(value);
    }

    // ── Awareness / presence ──────────────────────────────────────────────────
    async _broadcastPresence() {
        try {
            await this.connection.invoke('UpdateAwareness', this.documentId, {
                peerId: this.peerId, name: this.presence.name, color: this.presence.color });
        } catch (err) { console.warn('[CollabSession] presence failed', err); }
    }

    // state = { peerId: <connId>, data: { peerId, name, color }, lastUpdated }
    _onAwareness(state) {
        const d = (state && (state.data ?? state)) || {};
        const connId = state && (state.peerId ?? state.PeerId);
        if (!d.peerId) return;
        const entry = { name: d.name || 'Anonymous', color: d.color || '#888', connId };
        const isNew = !this.peers.has(d.peerId);
        this.peers.set(d.peerId, entry);
        if (connId) this.peersByConn.set(connId, { name: entry.name, color: entry.color });
        this._emitPeers();
        // A newcomer's broadcast reaches us (we're excluded from our own). Reply
        // once so they learn about us too. Converges (we don't reply to known peers).
        if (isNew && d.peerId !== this.peerId) this._broadcastPresence();
    }

    _onPeerGone(connId) {
        if (!connId) return;
        this.peersByConn.delete(connId);
        for (const [pid, e] of this.peers) if (e.connId === connId) this.peers.delete(pid);
        this._emitPeers();
    }

    _emitPeers() {
        this.onPeers([...this.peers.values()].map((e) => ({ name: e.name, color: e.color })));
    }

    // ── Comments ──────────────────────────────────────────────────────────────
    _norm(c) {
        if (!c) return null;
        const anchor = c.anchor ?? c.Anchor;
        const data = anchor && (anchor.data ?? anchor.Data);
        return {
            id: c.id ?? c.Id,
            body: c.body ?? c.Body,
            authorPeerId: c.authorPeerId ?? c.AuthorPeerId ?? '',
            nodeKey: data ? (data.key ?? data.Key ?? null) : null,
            resolved: (c.resolvedAt ?? c.ResolvedAt) != null,
            isOrphaned: c.isOrphaned ?? c.IsOrphaned ?? false,
        };
    }

    async _loadComments() {
        try {
            const list = await this.connection.invoke('ListOpenComments', this.documentId);
            for (const c of list || []) this._upsertComment(c);
        } catch (err) { console.warn('[CollabSession] ListOpenComments failed', err); }
    }

    async addComment(nodeKey, body) {
        // Hub: CreateComment(documentId, NewCommentCmd { Body, Anchor: AnchorDto(Kind, Data), ParentCommentId }).
        const dto = await this.connection.invoke('CreateComment', this.documentId,
            { body, anchor: { kind: ANCHOR_KIND, data: { key: nodeKey } }, parentCommentId: null });
        this._upsertComment(dto);
        return dto;
    }

    async resolveComment(commentId) {
        await this.connection.invoke('ResolveComment', this.documentId, commentId);
        this._removeComment(commentId);
    }

    // ListOpenComments returns resolved ones too, and resolves arrive as
    // ReceiveCommentUpdated → drop resolved here.
    _upsertComment(raw) {
        const dto = this._norm(raw);
        if (!dto || !dto.id) return;
        if (dto.resolved) this.comments.delete(dto.id);
        else this.comments.set(dto.id, dto);
        this._emitComments();
    }
    _removeComment(id) { if (id && this.comments.delete(id)) this._emitComments(); }
    _emitComments() { this.onComments([...this.comments.values()]); }
}
