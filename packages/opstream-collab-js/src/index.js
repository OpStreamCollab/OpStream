// opstream-collab — minimal client for OpStream's real-time collaboration server.
//
// It owns everything that is identical across editors — SignalR transport, the
// JoinDocument handshake, the per-path coalescing outbox, snapshot loading,
// presence/awareness and anchored comments — so an integration only has to say
// HOW its editor turns edits into ops and ops back into edits.
//
// You provide an `applyOps(ops, ctx)` callback (ops → your editor) and call
// `setPath()` / `delPath()` when the local editor changes. Presence and comments
// are opt-in. See the README for a full example.

import * as signalR from '@microsoft/signalr';

// ── base64 ⇄ utf-8 ──────────────────────────────────────────────────────────
// SignalR's JSON hub protocol expects byte[] params as a base64 string — it does
// NOT auto-encode a Uint8Array (you'd get {"0":..} which won't bind to a C#
// `byte[]`). So ops travel as base64(JSON).
export const b64ToUtf8 = (b64) =>
    new TextDecoder().decode(Uint8Array.from(atob(b64), (c) => c.charCodeAt(0)));

export const utf8ToB64 = (str) => {
    const arr = new TextEncoder().encode(str);
    let bin = '';
    for (let i = 0; i < arr.length; i++) bin += String.fromCharCode(arr[i]);
    return btoa(bin);
};

const opToPayload = (obj) => utf8ToB64(JSON.stringify(obj));
const randomPeerId = () => 'peer-' + Math.random().toString(36).slice(2, 10);

const noop = () => {};

/**
 * @typedef {Object} Op
 * @property {string}  path      register path, e.g. "nodes.k-ab12"
 * @property {*}       value     the value (undefined for deletes)
 * @property {boolean} isDelete  true for a tombstone
 * @property {string=} peerId    the sender's app peer id (null from a snapshot)
 * @property {number=} timestamp ms epoch (lets order-sensitive editors sort)
 */

export class OpStreamSession {
    /**
     * @param {Object}   opts
     * @param {string}   opts.url                SignalR hub URL, e.g. "/collab"
     * @param {string}   opts.documentId         shared document id
     * @param {string}   [opts.documentType="json"]
     * @param {number}   [opts.protocolVersion=1]
     * @param {(ops: Op[], ctx: {fromSnapshot: boolean}) => void} [opts.applyOps]
     *                   apply remote/snapshot ops to your editor.
     * @param {Object|null} [opts.presence]      { name, color, ... } enables presence; null disables.
     * @param {Object|null} [opts.comments]      { kind } enables anchored comments; null disables.
     * @param {(status: 'connecting'|'online'|'offline') => void} [opts.onStatus]
     * @param {(peers: Object[]) => void}        [opts.onPeers]   presence roster changed
     * @param {(comments: Object[]) => void}     [opts.onComments] open-comment list changed
     * @param {(op: Op) => void}                 [opts.onRemoteEdit] per remote op (edit feedback)
     * @param {string}   [opts.peerId]           override the generated app peer id
     * @param {number}   [opts.presenceHeartbeatMs=8000]
     */
    constructor(opts) {
        this.url = opts.url;
        this.documentId = opts.documentId;
        this.documentType = opts.documentType || 'json';
        this.protocolVersion = opts.protocolVersion ?? 1;

        this._applyOps = opts.applyOps || noop;
        this.onStatus = opts.onStatus || noop;
        this.onPeers = opts.onPeers || noop;
        this.onComments = opts.onComments || noop;
        this.onRemoteEdit = opts.onRemoteEdit || noop;

        this.presenceData = opts.presence || null;       // { name, color, ... } | null
        this.commentsOpts = opts.comments || null;        // { kind } | null
        this.presenceHeartbeatMs = opts.presenceHeartbeatMs ?? 8000;

        this.peerId = opts.peerId || randomPeerId();
        this.revision = 0;

        // Outbox: Map<path, op>. While a SendOp is in flight, repeated edits to the
        // same path collapse to the latest value — LWW-safe and the "send only the
        // final gesture state" behaviour (e.g. one op per drag, not one per tick).
        this._pending = new Map();
        this._flushing = false;

        this._peers = new Map();        // appPeerId -> { ...data, connId }
        this._peersByConn = new Map();  // connId    -> { ...data }
        this._comments = new Map();     // commentId -> normalized dto
        this._heartbeat = null;
        this.connection = null;
    }

    // ── lifecycle ─────────────────────────────────────────────────────────────
    async connect() {
        this.onStatus('connecting');
        this.connection = new signalR.HubConnectionBuilder()
            .withUrl(this.url)
            .withAutomaticReconnect()
            .build();

        this.connection.on('ReceiveOp', (payload, revision) => {
            this.revision = revision;
            this._applyRemoteBatch(JSON.parse(b64ToUtf8(payload)));
        });
        this.connection.onreconnected(() => this.onStatus('online'));
        this.connection.onreconnecting(() => this.onStatus('connecting'));
        this.connection.onclose(() => this.onStatus('offline'));

        if (this.presenceData) {
            this.connection.on('ReceiveAwarenessUpdate', (s) => this._onAwareness(s));
            this.connection.on('PeerDisconnected', (connId) => this._onPeerGone(connId));
        }
        if (this.commentsOpts) {
            this.connection.on('ReceiveCommentCreated', (d) => this._upsertComment(d));
            this.connection.on('ReceiveCommentUpdated', (d) => this._upsertComment(d));
            this.connection.on('ReceiveCommentDeleted', (m) =>
                this._removeComment(m && (m.commentId ?? m.CommentId ?? m)));
        }

        await this.connection.start();

        // We never receive our OWN awareness (server excludes the sender), so seed
        // our connId→data mapping now, else our own comments show the raw ConnectionId.
        if (this.presenceData && this.connection.connectionId) {
            this._peersByConn.set(this.connection.connectionId, { ...this.presenceData });
        }

        const join = await this.connection.invoke(
            'JoinDocument', this.documentId, this.documentType, this.protocolVersion);
        this.revision = join.revision;
        this._loadSnapshot(join.snapshot);
        for (const p of join.pendingOps || []) {
            this._applyRemoteBatch(JSON.parse(b64ToUtf8(p)));
        }

        if (this.presenceData) {
            await this._broadcastPresence();
            this._heartbeat = setInterval(() => this._broadcastPresence(), this.presenceHeartbeatMs);
        }
        if (this.commentsOpts) await this._loadComments();

        this.onStatus('online');
    }

    async disconnect() {
        if (this._heartbeat) { clearInterval(this._heartbeat); this._heartbeat = null; }
        if (this.connection) { try { await this.connection.stop(); } catch { /* ignore */ } }
    }

    // ── outbox (local edits → server) ───────────────────────────────────────────
    /** Queue a register set. `value` must be JSON-serializable. */
    setPath(path, value) {
        this._enqueue({ $type: 'set', path, value, timestamp: Date.now(), peerId: this.peerId });
    }

    /** Queue a register delete (tombstone). */
    delPath(path) {
        this._enqueue({ $type: 'del', path, timestamp: Date.now(), peerId: this.peerId });
    }

    _enqueue(op) {
        this._pending.set(op.path, op);
        this._flush();
    }

    async _flush() {
        if (this._flushing) return;
        this._flushing = true;
        try {
            while (this._pending.size > 0) {
                // Snapshot+clear so enqueues during the await coalesce into the next pass.
                const batch = { operations: Array.from(this._pending.values()) };
                this._pending.clear();
                try {
                    const r = await this.connection.invoke(
                        'SendOp', this.documentId, opToPayload(batch), this.revision);
                    if (r && r.success) this.revision = r.newRevision;
                } catch (err) {
                    console.error('[opstream] SendOp failed:', err);
                    break;
                }
            }
        } finally {
            this._flushing = false;
        }
    }

    // ── snapshot / remote ops → editor ──────────────────────────────────────────
    _loadSnapshot(snapshotB64) {
        if (!snapshotB64) return;
        const doc = JSON.parse(b64ToUtf8(snapshotB64));
        const regs = doc.registers || {};
        const ops = Object.entries(regs)
            .filter(([, r]) => !r.isDeleted)
            .map(([path, r]) => ({
                path, value: r.value, isDelete: false,
                peerId: r.peerId ?? null, timestamp: r.timestamp ?? 0,
            }));
        if (ops.length) this._applyOps(ops, { fromSnapshot: true });
    }

    _applyRemoteBatch(batch) {
        const ops = (batch.operations || []).map((o) => ({
            path: o.path, value: o.value, isDelete: o.$type === 'del',
            peerId: o.peerId ?? null, timestamp: o.timestamp ?? 0,
        }));
        if (!ops.length) return;
        this._applyOps(ops, { fromSnapshot: false });
        // Per-op edit feedback (skip our own echoes).
        for (const op of ops) {
            if (op.peerId && op.peerId !== this.peerId) this.onRemoteEdit(op);
        }
    }

    // ── presence / awareness ────────────────────────────────────────────────────
    /** App peer that authored an op (from op.peerId). */
    getPeer(peerId) { return this._peers.get(peerId) || null; }
    /** Presence behind a server ConnectionId (e.g. a comment's authorPeerId). */
    getPeerByConn(connId) { return this._peersByConn.get(connId) || null; }
    /** Current roster (your own entry included). */
    get peers() { return [...this._peers.values()]; }

    async _broadcastPresence() {
        try {
            await this.connection.invoke('UpdateAwareness', this.documentId,
                { peerId: this.peerId, ...this.presenceData });
        } catch (err) { console.warn('[opstream] presence failed', err); }
    }

    // state = { peerId: <connId>, data: { peerId, ...payload }, lastUpdated }
    _onAwareness(state) {
        const d = (state && (state.data ?? state)) || {};
        const connId = state && (state.peerId ?? state.PeerId);
        const appId = d.peerId;
        if (!appId) return;
        const entry = { ...d, connId };
        const isNew = !this._peers.has(appId);
        this._peers.set(appId, entry);
        if (connId) this._peersByConn.set(connId, { ...d });
        this.onPeers(this.peers);
        // A newcomer's broadcast reaches us (we're excluded from our own). Reply once
        // so they learn about us too; converges since we don't reply to known peers.
        if (isNew && appId !== this.peerId) this._broadcastPresence();
    }

    _onPeerGone(connId) {
        if (!connId) return;
        this._peersByConn.delete(connId);
        for (const [id, e] of this._peers) if (e.connId === connId) this._peers.delete(id);
        this.onPeers(this.peers);
    }

    // ── comments ─────────────────────────────────────────────────────────────────
    /** Normalize the server Comment DTO (SignalR casing varies) to a stable shape. */
    _normComment(c) {
        if (!c) return null;
        const anchor = c.anchor ?? c.Anchor;
        return {
            id: c.id ?? c.Id,
            body: c.body ?? c.Body,
            authorPeerId: c.authorPeerId ?? c.AuthorPeerId ?? '',
            anchor: anchor ? { kind: anchor.kind ?? anchor.Kind, data: anchor.data ?? anchor.Data } : null,
            resolved: (c.resolvedAt ?? c.ResolvedAt) != null,
            isOrphaned: c.isOrphaned ?? c.IsOrphaned ?? false,
            raw: c,
        };
    }

    /** Open (unresolved) comments. */
    get comments() { return [...this._comments.values()]; }

    async _loadComments() {
        try {
            const list = await this.connection.invoke('ListOpenComments', this.documentId);
            for (const c of list || []) this._upsertComment(c);
        } catch (err) { console.warn('[opstream] ListOpenComments failed', err); }
    }

    /**
     * Create an anchored comment. `anchorData` is your editor's anchor payload
     * (e.g. { key } or { objectId }); it's wrapped with the configured kind.
     */
    async addComment(anchorData, body, parentCommentId = null) {
        const kind = this.commentsOpts?.kind || 'generic';
        const dto = await this.connection.invoke('CreateComment', this.documentId,
            { body, anchor: { kind, data: anchorData }, parentCommentId });
        this._upsertComment(dto);
        return this._normComment(dto);
    }

    async resolveComment(commentId) {
        await this.connection.invoke('ResolveComment', this.documentId, commentId);
        this._removeComment(commentId);
    }

    // ListOpenComments can include resolved ones, and resolves arrive as
    // ReceiveCommentUpdated → drop resolved here so the list stays "open only".
    _upsertComment(raw) {
        const dto = this._normComment(raw);
        if (!dto || !dto.id) return;
        if (dto.resolved) this._comments.delete(dto.id);
        else this._comments.set(dto.id, dto);
        this.onComments(this.comments);
    }

    _removeComment(id) {
        if (id && this._comments.delete(id)) this.onComments(this.comments);
    }
}

export default OpStreamSession;
