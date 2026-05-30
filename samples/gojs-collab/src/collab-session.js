import { OpStreamSession } from 'opstream-collab';

// GoJS adapter for OpStream. The transport, outbox, snapshot, presence and
// comment plumbing all live in `opstream-collab`; this file only describes the
// GoJS-specific bits:
//   • Capture — diff the model vs a baseline on each finished transaction and
//     emit set/del per changed node/link key.
//   • Apply   — mutate the model inside a guarded commit (nodes before links).
//   • Anchor  — comments attach to a node by its stable key (kind 'gojs-node').
//
// Sync layout: each node is a register at `nodes.<key>`, each link at `links.<key>`.

const N = 'nodes.';
const L = 'links.';
const ANCHOR_KIND = 'gojs-node'; // node keys are stable → no anchor rebasing needed

export class CollabSession {
    constructor({ url, documentId, diagram, presence,
                  onStatus, onPeers, onRemoteEdit, onComments }) {
        this.diagram = diagram;
        this.model = diagram.model;
        this._onRemoteEdit = onRemoteEdit || (() => {});

        this.remoteApply = false;
        this.lastNodes = new Map(); // key -> JSON.stringify(nodeData)
        this.lastLinks = new Map();

        this.session = new OpStreamSession({
            url, documentId,
            presence,
            comments: { kind: ANCHOR_KIND },
            onStatus,
            onPeers: (peers) => (onPeers || (() => {}))(
                peers.map((p) => ({ name: p.name, color: p.color }))),
            onComments: (list) => (onComments || (() => {}))(
                list.map((c) => ({
                    id: c.id, body: c.body, authorPeerId: c.authorPeerId,
                    nodeKey: c.anchor && c.anchor.data ? (c.anchor.data.key ?? null) : null,
                }))),
            onRemoteEdit: (op) => {
                if (!op.path.startsWith(N)) return; // only flash node edits
                const peer = this.session.getPeer(op.peerId) || { name: 'Someone', color: '#9aa3ad' };
                this._onRemoteEdit(op.path.slice(N.length), peer, op.isDelete ? 'del' : 'set');
            },
            applyOps: (ops) => this._applyOps(ops),
        });
    }

    async connect() {
        await this.session.connect();
        // Start capturing only after the snapshot/pending ops have been applied.
        this._snapshotBaseline();
        this.diagram.addModelChangedListener((e) => {
            if (e.isTransactionFinished && !this.remoteApply) this._captureChanges();
        });
    }

    // Map a comment's author ConnectionId to its presence ({name,color}).
    peerByConn(connId) { return this.session.getPeerByConn(connId); }
    addComment(nodeKey, body) { return this.session.addComment({ key: nodeKey }, body); }
    resolveComment(id) { return this.session.resolveComment(id); }

    // ── Capture: diff model vs baseline → ops ───────────────────────────────────
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
                this.session.setPath(prefix + key, JSON.parse(js));
            }
        }
        for (const key of [...last.keys()]) {
            if (!seen.has(key)) {
                last.delete(key);
                this.session.delPath(prefix + key);
            }
        }
    }

    // ── Apply: remote/snapshot ops → model (nodes before links) ─────────────────
    _applyOps(ops) {
        const nodeOps = ops.filter((o) => o.path.startsWith(N));
        const linkOps = ops.filter((o) => o.path.startsWith(L));
        if (!nodeOps.length && !linkOps.length) return;
        this.remoteApply = true;
        try {
            // Model.commit passes the Model (Diagram.commit would pass the Diagram).
            this.model.commit((m) => {
                for (const o of nodeOps) this._applyNode(m, o.path.slice(N.length), o.value, o.isDelete);
                for (const o of linkOps) this._applyLink(m, o.path.slice(L.length), o.value, o.isDelete);
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
