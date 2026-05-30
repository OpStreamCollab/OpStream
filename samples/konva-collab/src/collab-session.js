import { OpStreamSession } from 'opstream-collab';

// Konva adapter for OpStream. Transport/outbox/snapshot/presence/comments live in
// `opstream-collab`; here we only map Konva nodes ⇄ ops.
//
// Schema: one register per shape → `shapes.<uuid>` = { className, attrs }.
// Capture: main.js calls emitShape()/emitDelete() from dragend/transformend/etc.
// Apply: setAttrs() on an existing node, or new Konva[className](attrs) for new
//   ones. Event delegation on the stage keeps remote shapes selectable/draggable.

const PREFIX = 'shapes.';
const ANCHOR_KIND = 'konva-shape';

export class CollabSession {
    constructor({ url, documentId, layer, presence,
                  onStatus, onPeers, onRemoteEdit, onRemoteDelete, onComments }) {
        this.layer = layer;
        this._onRemoteEdit = onRemoteEdit || (() => {});   // (shapeId, {name,color})
        this._onRemoteDelete = onRemoteDelete || (() => {}); // (shapeId)
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
                    id: c.id, body: c.body, authorPeerId: c.authorPeerId,
                    shapeId: c.anchor && c.anchor.data ? (c.anchor.data.id ?? null) : null,
                    resolved: c.resolved, isOrphaned: c.isOrphaned,
                }))),
            applyOps: (ops, ctx) => this._applyOps(ops, ctx),
        });
    }

    get isApplyingRemote() { return this.remoteApplyDepth > 0; }
    peerByOp(id) { return this.session.getPeer(id) || { name: 'Someone', color: '#9aa3ad' }; }
    peerByConn(id) { return this.session.getPeerByConn(id); }

    connect() { return this.session.connect(); }
    addComment(shapeId, body) { return this.session.addComment({ id: shapeId }, body); }
    resolveComment(id) { return this.session.resolveComment(id); }

    // ── Public capture API (called from main.js) ────────────────────────────────
    emitShape(node) {
        if (this.isApplyingRemote) return;
        const id = node.id();
        if (!id) return;
        const className = node.getClassName();
        let attrs = node.getAttrs();
        // HTMLImageElement isn't JSON-serializable. Strip it; stash URL as _src.
        if (className === 'Image') {
            const { image: _, ...rest } = attrs;
            attrs = { ...rest, _src: node._srcUrl || '' };
        }
        this.session.setPath(PREFIX + id, { className, attrs });
    }

    emitDelete(id) {
        if (this.isApplyingRemote || !id) return;
        this.session.delPath(PREFIX + id);
    }

    // ── Apply remote / snapshot ─────────────────────────────────────────────────
    _applyOps(ops, { fromSnapshot }) {
        this.remoteApplyDepth++;
        try {
            for (const op of ops) {
                if (!op.path.startsWith(PREFIX)) continue;
                const id = op.path.slice(PREFIX.length);
                if (op.isDelete) this._applyDel(id);
                else this._applySet(id, op.value, fromSnapshot ? null : op.peerId, fromSnapshot);
            }
            this.layer.batchDraw();
        } finally {
            this.remoteApplyDepth--;
        }
    }

    _applySet(id, value, fromPeerId, fromSnapshot) {
        const existing = this.layer.findOne('#' + id);
        if (existing) {
            existing.setAttrs(value.attrs);
        } else {
            const node = this._createShape(value.className, value.attrs);
            if (!node) return;
            this.layer.add(node);
        }
        if (!fromSnapshot && fromPeerId && fromPeerId !== this.session.peerId) {
            this._onRemoteEdit(id, this.peerByOp(fromPeerId));
        }
    }

    _applyDel(id) {
        const node = this.layer.findOne('#' + id);
        if (node) {
            node.destroy();
            this._onRemoteDelete(id);
        }
    }

    _createShape(className, attrs) {
        const Konva = window.Konva;

        // Images load async — placeholder rect, swap on load.
        if (className === 'Image') {
            const { _src: src, image: _, ...rest } = attrs;
            const placeholder = new Konva.Rect({
                fill: '#e8edf2', stroke: '#b0bec5', strokeWidth: 1,
                draggable: true, name: 'shape', ...rest,
            });
            if (src) {
                const domImg = new window.Image();
                domImg.crossOrigin = 'anonymous';
                domImg.onload = () => {
                    const kImg = new Konva.Image({
                        ...rest, image: domImg, draggable: true, name: 'shape',
                    });
                    kImg._srcUrl = src;
                    const idx = placeholder.getZIndex();
                    placeholder.destroy();
                    this.layer.add(kImg);
                    kImg.zIndex(idx);
                    this.layer.batchDraw();
                };
                domImg.onerror = () => { placeholder.fill('#ffebee'); this.layer.batchDraw(); };
                domImg.src = src;
            }
            return placeholder;
        }

        const cls = {
            Rect: Konva.Rect, Circle: Konva.Circle, Ellipse: Konva.Ellipse,
            Text: Konva.Text, Line: Konva.Line, Arrow: Konva.Arrow,
            RegularPolygon: Konva.RegularPolygon, Star: Konva.Star,
        }[className];
        if (!cls) { console.warn('[collab] unknown shape class:', className); return null; }
        // draggable + name always set so event delegation works for remote shapes.
        return new cls({ draggable: true, name: 'shape', ...attrs });
    }
}
