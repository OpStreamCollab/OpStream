import { OpStreamSession } from 'opstream-collab';

// LiteGraph adapter for OpStream. Transport/outbox/snapshot live in
// `opstream-collab`; here we only map graph edits ⇄ ops.
//
// Hybrid schema (LiteGraph's change callback is coarse):
//   • Structure (add / remove / connection) → one register `graph` holding the
//     full graph.serialize(); applied with graph.configure() (preserves ids, so
//     links rebuild correctly).
//   • Dragging → granular `nodes.<id>.pos` ops, so a drag doesn't ship the whole
//     graph and doesn't fight a concurrent structural edit.

const GRAPH_PATH = 'graph';
const POS_PREFIX = 'nodes.'; // nodes.<id>.pos

export class CollabSession {
    constructor({ url, documentId, graph, canvas, onStatus }) {
        this.graph = graph;
        this.canvas = canvas;
        this.remoteApplyDepth = 0; // >0 while applying remote ops (suppress echo)

        this.session = new OpStreamSession({
            url, documentId,
            onStatus,
            applyOps: (ops) => this._applyOps(ops),
        });
    }

    async connect() {
        await this.session.connect();
        this._installHooks();
    }

    // ── Capture ─────────────────────────────────────────────────────────────────
    _installHooks() {
        const self = this;
        this.graph.onNodeAdded = () => self._structureChanged();
        this.graph.onNodeRemoved = () => self._structureChanged();
        this.graph.onConnectionChange = () => self._structureChanged();
        if (this.canvas) this.canvas.onNodeMoved = (node) => self._nodeMoved(node);
    }

    _structureChanged() {
        if (this.remoteApplyDepth > 0) return;
        this.session.setPath(GRAPH_PATH, this.graph.serialize());
    }

    _nodeMoved(node) {
        if (this.remoteApplyDepth > 0 || !node) return;
        this.session.setPath(`${POS_PREFIX}${node.id}.pos`, [node.pos[0], node.pos[1]]);
    }

    // ── Apply (full-graph register before per-node positions) ───────────────────
    _applyOps(ops) {
        const ordered = [...ops].sort(
            (a, b) => (a.path === GRAPH_PATH ? -1 : 0) - (b.path === GRAPH_PATH ? -1 : 0));
        for (const op of ordered) this._applyPath(op.path, op.value, op.isDelete);
    }

    _applyPath(path, value, isDelete) {
        if (path === GRAPH_PATH) {
            if (isDelete) return;
            this.remoteApplyDepth++;
            try {
                this.graph.configure(value); // rebuilds nodes + links, preserving ids
                this.graph.setDirtyCanvas(true, true);
            } finally {
                // configure fires onNodeAdded as microtasks-after; defer the decrement
                // one tick so those are recognised as remote (not re-captured).
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
