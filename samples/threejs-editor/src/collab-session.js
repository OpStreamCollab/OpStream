import { OpStreamSession } from 'opstream-collab';

// three.js editor adapter for OpStream. Transport/outbox/snapshot live in
// `opstream-collab`; here we only map the editor's command system ⇄ ops.
//
// We wrap editor.history.execute so every local command is applied locally (as
// before) and the supported ones are translated to per-property JSON ops at
// `objects.<uuid>[.subpath]`. Remote ops mutate the existing object in place and
// dispatch the editor's own signals — so identity, selection and the full editor
// UI are preserved.

const PATH_PREFIX = 'objects.';

// Commands whose toJSON we know how to map. Anything else runs locally but is NOT
// shared — intentional for the demo, so unsupported edits don't crash the peer.
const SUPPORTED_COMMANDS = new Set([
    'AddObjectCommand',
    'RemoveObjectCommand',
    'MoveObjectCommand',
    'SetPositionCommand',
    'SetRotationCommand',
    'SetScaleCommand',
    'SetColorCommand',
    'SetValueCommand',
]);

export class CollabSession {
    constructor({ url, documentId, editor, editorWindow, onStatus }) {
        this.editor = editor;
        this.editorWindow = editorWindow;
        this.THREE = editorWindow.THREE;
        this.remoteApplyDepth = 0;
        this._originalExecute = null;

        this.session = new OpStreamSession({
            url, documentId,
            onStatus,
            applyOps: (ops, ctx) => this._applyOps(ops, ctx),
        });
    }

    get peerId() { return this.session.peerId; }

    async connect() {
        await this.session.connect();
        this._installHistoryHook();
    }

    // ── History hook: capture local commands → ops ──────────────────────────────
    _installHistoryHook() {
        const history = this.editor.history;
        const original = history.execute.bind(history);
        this._originalExecute = original;
        history.execute = (cmd, optionalName) => {
            original(cmd, optionalName);
            if (this.remoteApplyDepth > 0) return;
            const ops = this._commandToOps(cmd);
            if (ops) for (const op of ops) {
                if (op.$type === 'del') this.session.delPath(op.path);
                else this.session.setPath(op.path, op.value);
            }
        };
    }

    _commandToOps(cmd) {
        if (!SUPPORTED_COMMANDS.has(cmd.type)) return null;
        if (!cmd.object) return null;
        const uuid = cmd.object.uuid;
        const set = (path, value) => ({ $type: 'set', path, value });

        switch (cmd.type) {
            case 'AddObjectCommand':
                return [set(PATH_PREFIX + uuid, cmd.object.toJSON())];
            case 'RemoveObjectCommand':
                return [{ $type: 'del', path: PATH_PREFIX + uuid }];
            case 'MoveObjectCommand':
                return [set(PATH_PREFIX + uuid + '.parent', cmd.newParent.uuid)];
            case 'SetPositionCommand':
                return [set(PATH_PREFIX + uuid + '.position', cmd.newPosition.toArray())];
            case 'SetRotationCommand':
                return [set(PATH_PREFIX + uuid + '.rotation', cmd.newRotation.toArray())];
            case 'SetScaleCommand':
                return [set(PATH_PREFIX + uuid + '.scale', cmd.newScale.toArray())];
            case 'SetColorCommand':
                return [set(PATH_PREFIX + uuid + '.color.' + cmd.attributeName, cmd.newValue)];
            case 'SetValueCommand':
                return [set(PATH_PREFIX + uuid + '.attr.' + cmd.attributeName, cmd.newValue)];
            default:
                return null;
        }
    }

    // ── Apply remote / snapshot ─────────────────────────────────────────────────
    _applyOps(ops, { fromSnapshot }) {
        // Apply creation before sub-property overrides. Snapshots arrive unordered,
        // so sort by (timestamp, peerId); live batches are already causal.
        const ordered = fromSnapshot
            ? [...ops].sort((a, b) =>
                (a.timestamp - b.timestamp) || String(a.peerId).localeCompare(String(b.peerId)))
            : ops;
        this.remoteApplyDepth++;
        try {
            for (const op of ordered) this._applyPath(op.path, op.value, op.isDelete);
        } finally {
            this.remoteApplyDepth--;
        }
    }

    _applyPath(path, value, isDelete) {
        if (!path.startsWith(PATH_PREFIX)) return;
        const rest = path.slice(PATH_PREFIX.length);
        const dotIdx = rest.indexOf('.');
        const uuid = dotIdx === -1 ? rest : rest.slice(0, dotIdx);
        const subPath = dotIdx === -1 ? '' : rest.slice(dotIdx + 1);
        const editor = this.editor;

        if (subPath === '') {
            const existing = editor.objectByUuid(uuid);
            if (isDelete) {
                if (existing) editor.removeObject(existing);
                return;
            }
            if (existing) return; // idempotent — creation already replayed
            const loader = new this.THREE.ObjectLoader();
            const obj = loader.parse(value);
            editor.addObject(obj);
            return;
        }

        const obj = editor.objectByUuid(uuid);
        if (!obj) return; // object not yet known on this peer — drop silently

        switch (subPath) {
            case 'position':
                obj.position.fromArray(value);
                obj.updateMatrixWorld(true);
                editor.signals.objectChanged.dispatch(obj);
                return;
            case 'rotation':
                obj.rotation.fromArray(value);
                obj.updateMatrixWorld(true);
                editor.signals.objectChanged.dispatch(obj);
                return;
            case 'scale':
                obj.scale.fromArray(value);
                obj.updateMatrixWorld(true);
                editor.signals.objectChanged.dispatch(obj);
                return;
            case 'parent': {
                const newParent = editor.objectByUuid(value) || editor.scene;
                if (obj.parent !== newParent) {
                    if (obj.parent) obj.parent.remove(obj);
                    newParent.add(obj);
                    editor.signals.sceneGraphChanged.dispatch();
                }
                return;
            }
            default:
                if (subPath.startsWith('color.')) {
                    const attr = subPath.slice('color.'.length);
                    if (obj[attr] && typeof obj[attr].setHex === 'function') {
                        obj[attr].setHex(value);
                        editor.signals.objectChanged.dispatch(obj);
                    }
                    return;
                }
                if (subPath.startsWith('attr.')) {
                    const attr = subPath.slice('attr.'.length);
                    obj[attr] = value;
                    editor.signals.objectChanged.dispatch(obj);
                    return;
                }
                return; // unhandled sub-path — quietly ignore
        }
    }
}
