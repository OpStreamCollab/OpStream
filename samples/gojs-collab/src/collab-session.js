import * as signalR from '@microsoft/signalr';

// Collaborative GoJS flowchart over OpStream's JSON CRDT engine.
//
// Each node is a register at `nodes.<key>`, each link at `links.<key>`. We use
// string uuid keys (GoJS' default integer keys are per-model and would collide
// across peers). Capture: on each finished transaction we diff the model against
// our last-known snapshot and emit set/del per changed key (robust — no reliance
// on ChangedEvent internals). Apply: model mutations inside a guarded commit.
//
// GoJS is commercial; the free evaluation build works identically but watermarks
// the canvas. See README.

const PROTOCOL_VERSION = 1;
const DOCUMENT_TYPE = 'json';
const N = 'nodes.';
const L = 'links.';

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
    constructor({ url, documentId, diagram, onStatus, onPeerId }) {
        this.url = url;
        this.documentId = documentId;
        this.diagram = diagram;
        this.model = diagram.model;
        this.onStatus = onStatus || (() => {});
        this.onPeerId = onPeerId || (() => {});

        this.peerId = randomPeerId();
        this.revision = 0;

        this.remoteApply = false;
        this.lastNodes = new Map(); // key -> JSON.stringify(nodeData)
        this.lastLinks = new Map();
        this.pending = new Map();
        this.flushing = false;

        this.connection = null;
    }

    async connect() {
        this.onStatus('connecting');
        this.onPeerId(this.peerId);

        this.connection = new signalR.HubConnectionBuilder()
            .withUrl(this.url).withAutomaticReconnect().build();

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
        for (const p of joinResult.pendingOps || []) {
            this._applyRemoteBatch(JSON.parse(b64ToUtf8(p)));
        }

        this._snapshotBaseline();
        this.diagram.addModelChangedListener((e) => {
            if (e.isTransactionFinished && !this.remoteApply) this._captureChanges();
        });
        this.onStatus('online');
    }

    // ── Capture: diff the model against our baseline ──────────────────────────
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

    // ── Apply remote / snapshot ───────────────────────────────────────────────
    _loadSnapshot(snapshotB64) {
        if (!snapshotB64) return;
        const doc = JSON.parse(b64ToUtf8(snapshotB64));
        const regs = doc.registers || {};
        const ops = Object.entries(regs)
            .filter(([, r]) => !r.isDeleted)
            .map(([path, r]) => ({ path, value: r.value, del: false }));
        this._applyOrdered(ops);
    }

    _applyRemoteBatch(opBatch) {
        const ops = (opBatch.operations || []).map((o) => ({ path: o.path, value: o.value, del: o.$type === 'del' }));
        this._applyOrdered(ops);
    }

    // Nodes before links (a link needs both endpoints to exist).
    _applyOrdered(ops) {
        const nodeOps = ops.filter((o) => o.path.startsWith(N));
        const linkOps = ops.filter((o) => o.path.startsWith(L));
        if (!nodeOps.length && !linkOps.length) return;
        this.remoteApply = true;
        try {
            // Model.commit pasa el Model (los _applyNode/_applyLink usan métodos del Model).
            this.diagram.model.commit((m) => {
                for (const o of nodeOps) this._applyNode(m, o.path.slice(N.length), o.value, o.del);
                for (const o of linkOps) this._applyLink(m, o.path.slice(L.length), o.value, o.del);
            }, 'remote');
        } finally {
            this.remoteApply = false;
            this._snapshotBaseline(); // adopt applied state so it isn't re-sent
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
}
