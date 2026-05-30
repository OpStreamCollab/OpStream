import * as signalR from '@microsoft/signalr';

// Collaborative LiteGraph over OpStream's JSON CRDT engine.
//
// Strategy (LiteGraph's connection callback is coarse — it only says "this node
// changed", not which link): a robust hybrid.
//   • Structure (add / remove / connection) → one register `graph` holding the
//     full `graph.serialize()`. Applied remotely with `graph.configure()`, which
//     preserves node ids, so links rebuild correctly.
//   • Dragging → granular `nodes.<id>.pos` ops, so a drag doesn't ship the whole
//     graph every frame and doesn't fight a concurrent structural edit.
//
// Same shape as the three.js / Luckysheet samples — only the capture/apply glue
// is editor-specific.

const PROTOCOL_VERSION = 1;
const DOCUMENT_TYPE = 'json';
const GRAPH_PATH = 'graph';
const POS_PREFIX = 'nodes.';   // nodes.<id>.pos

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
    constructor({ url, documentId, graph, canvas, onStatus, onPeerId }) {
        this.url = url;
        this.documentId = documentId;
        this.graph = graph;
        this.canvas = canvas;
        this.onStatus = onStatus || (() => {});
        this.onPeerId = onPeerId || (() => {});

        this.peerId = randomPeerId();
        this.revision = 0;

        this.remoteApplyDepth = 0;   // >0 while applying remote ops (suppress echo)
        this.pending = new Map();    // per-path latest-wins outbox
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
        this.graph.onNodeAdded = () => self._structureChanged();
        this.graph.onNodeRemoved = () => self._structureChanged();
        this.graph.onConnectionChange = () => self._structureChanged();
        if (this.canvas) {
            this.canvas.onNodeMoved = (node) => self._nodeMoved(node);
        }
    }

    _structureChanged() {
        if (this.remoteApplyDepth > 0) return;
        this._enqueue({
            $type: 'set', path: GRAPH_PATH, value: this.graph.serialize(),
            timestamp: Date.now(), peerId: this.peerId,
        });
    }

    _nodeMoved(node) {
        if (this.remoteApplyDepth > 0 || !node) return;
        const path = `${POS_PREFIX}${node.id}.pos`;
        this._enqueue({
            $type: 'set', path, value: [node.pos[0], node.pos[1]],
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
        // Apply the full-graph register first so per-node positions land on
        // nodes that already exist.
        if (registers[GRAPH_PATH] && !registers[GRAPH_PATH].isDeleted) {
            this._applyPath(GRAPH_PATH, registers[GRAPH_PATH].value, false);
        }
        for (const [path, reg] of Object.entries(registers)) {
            if (path === GRAPH_PATH || reg.isDeleted) continue;
            this._applyPath(path, reg.value, false);
        }
    }

    _applyRemoteBatch(opBatch) {
        const ops = opBatch.operations || [];
        // Graph-structure ops before position ops, same reason as above.
        const ordered = [...ops].sort(
            (a, b) => (a.path === GRAPH_PATH ? -1 : 0) - (b.path === GRAPH_PATH ? -1 : 0)
        );
        for (const op of ordered) this._applyPath(op.path, op.value, op.$type === 'del');
    }

    _applyPath(path, value, isDelete) {
        if (path === GRAPH_PATH) {
            if (isDelete) return;
            this.remoteApplyDepth++;
            try {
                this.graph.configure(value);   // rebuilds nodes + links, preserving ids
                this.graph.setDirtyCanvas(true, true);
            } finally {
                // configure fires onNodeAdded as microtasks-after; defer the
                // decrement one tick so those are recognised as remote.
                Promise.resolve().then(() => { this.remoteApplyDepth--; });
            }
            return;
        }

        if (path.startsWith(POS_PREFIX)) {
            const id = Number(path.slice(POS_PREFIX.length).split('.')[0]);
            const node = this.graph.getNodeById(id);
            if (node && Array.isArray(value)) {
                // Setting pos programmatically does not fire onNodeMoved, so no echo.
                node.pos[0] = value[0];
                node.pos[1] = value[1];
                this.graph.setDirtyCanvas(true, true);
            }
        }
    }
}
