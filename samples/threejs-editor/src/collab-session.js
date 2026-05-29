import * as signalR from '@microsoft/signalr';

const PROTOCOL_VERSION = 1;
const DOCUMENT_TYPE = 'json';
const PATH_PREFIX = 'objects.';

// Commands whose toJSON we know how to map to JsonOpBatch entries.
// Any other command runs locally but is silently NOT shared — intentional
// for the demo, so unsupported edits don't crash the peer.
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

const b64ToUtf8 = (b64) => new TextDecoder().decode(
    Uint8Array.from(atob(b64), (c) => c.charCodeAt(0))
);
const utf8ToB64 = (str) => {
    const arr = new TextEncoder().encode(str);
    let bin = '';
    for (let i = 0; i < arr.length; i++) bin += String.fromCharCode(arr[i]);
    return btoa(bin);
};
// SignalR's JSON hub protocol expects byte[] params encoded as a base64 string —
// it does NOT auto-encode Uint8Array (you get {"0":..,"1":..} which won't bind
// to the server's `byte[] payload` parameter).
const opToPayload = (obj) => utf8ToB64(JSON.stringify(obj));

const randomPeerId = () =>
    'peer-' + Math.random().toString(36).slice(2, 10);

export class CollabSession {
    constructor({ url, documentId, editor, editorWindow, onStatus, onPeerId }) {
        this.url = url;
        this.documentId = documentId;
        this.editor = editor;
        this.editorWindow = editorWindow;
        this.THREE = editorWindow.THREE;
        this.onStatus = onStatus || (() => {});
        this.onPeerId = onPeerId || (() => {});

        this.peerId = randomPeerId();
        this.revision = 0;

        // Race guards / queue. The outbox is a Map<path, op> rather than an array:
        // while a SendOp is in flight, repeated edits on the same path (e.g. one
        // SetPositionCommand per drag-tick) collapse to the latest value. This is
        // LWW-safe under the Json engine and gives us the "send only the final
        // gesture state" behaviour without needing explicit gesture-end signals.
        this.remoteApplyDepth = 0;
        this.pending = new Map();
        this.flushing = false;

        this.connection = null;
        this._originalExecute = null;
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
            const opJson = JSON.parse(b64ToUtf8(payload));
            this._applyRemoteBatch(opJson);
        });

        await this.connection.start();

        const joinResult = await this.connection.invoke(
            'JoinDocument', this.documentId, DOCUMENT_TYPE, PROTOCOL_VERSION,
        );

        this.revision = joinResult.revision;
        this._loadSnapshot(joinResult.snapshot);

        // Any ops the server already broadcast between snapshot capture and our
        // subscription land in pendingOps. Apply in order so we converge.
        for (const pendingPayload of joinResult.pendingOps || []) {
            this._applyRemoteBatch(JSON.parse(b64ToUtf8(pendingPayload)));
        }

        this._installHistoryHook();
        this.onStatus('online');
    }

    // ─────────────────────────────────────────────────────────────────────
    // History hook: wrap editor.history.execute so every local command is
    // both applied locally (as before) and queued for the server.
    // ─────────────────────────────────────────────────────────────────────
    _installHistoryHook() {
        const history = this.editor.history;
        const original = history.execute.bind(history);
        this._originalExecute = original;

        history.execute = (cmd, optionalName) => {
            original(cmd, optionalName);
            if (this.remoteApplyDepth > 0) return;
            const ops = this._commandToOps(cmd);
            if (ops && ops.length) this._enqueueOps(ops);
        };
    }

    _commandToOps(cmd) {
        if (!SUPPORTED_COMMANDS.has(cmd.type)) return null;
        if (!cmd.object) return null;
        const uuid = cmd.object.uuid;
        const ts = Date.now();
        // The Json engine's polymorphic discriminator is `$type` (System.Text.Json
        // default). The wire-protocol docs show plain `type` which is out of date —
        // it must be `$type` or the server fails to bind the op variant.
        const set = (path, value) => ({
            $type: 'set', path, value, timestamp: ts, peerId: this.peerId,
        });

        switch (cmd.type) {
            case 'AddObjectCommand':
                return [set(PATH_PREFIX + uuid, cmd.object.toJSON())];
            case 'RemoveObjectCommand':
                return [{ $type: 'del', path: PATH_PREFIX + uuid, timestamp: ts, peerId: this.peerId }];
            case 'MoveObjectCommand':
                return [set(PATH_PREFIX + uuid + '.parent', cmd.newParent.uuid)];
            case 'SetPositionCommand':
                return [set(PATH_PREFIX + uuid + '.position', cmd.newPosition.toArray())];
            case 'SetRotationCommand':
                return [set(PATH_PREFIX + uuid + '.rotation', cmd.newRotation.toArray())];
            case 'SetScaleCommand':
                return [set(PATH_PREFIX + uuid + '.scale', cmd.newScale.toArray())];
            case 'SetColorCommand':
                // attributeName names a THREE.Color attribute (e.g. "color" on materials
                // when applied to a Mesh's material — for the demo we keep it generic).
                return [set(PATH_PREFIX + uuid + '.color.' + cmd.attributeName, cmd.newValue)];
            case 'SetValueCommand':
                return [set(PATH_PREFIX + uuid + '.attr.' + cmd.attributeName, cmd.newValue)];
            default:
                return null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Outbox with per-path latest-wins coalescing. While a SendOp is in flight,
    // new ops on the same path overwrite the pending one — so dragging an
    // object emits one SendOp per network round-trip carrying only the latest
    // position, not one per tick.
    // ─────────────────────────────────────────────────────────────────────
    _enqueueOps(ops) {
        for (const op of ops) this.pending.set(op.path, op);
        this._flush();
    }

    async _flush() {
        if (this.flushing) return;
        this.flushing = true;
        try {
            while (this.pending.size > 0) {
                // Snapshot and clear so further enqueues during the await accumulate
                // for the next iteration (the natural coalescing window).
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

    // ─────────────────────────────────────────────────────────────────────
    // Snapshot / remote-op application
    // ─────────────────────────────────────────────────────────────────────
    _loadSnapshot(snapshotB64) {
        if (!snapshotB64) return;
        const doc = JSON.parse(b64ToUtf8(snapshotB64));
        const registers = doc.registers || {};

        // Apply in (timestamp, peerId) order so creation comes before sub-property
        // overrides (e.g. Add then later SetPosition on the same uuid).
        const entries = Object.entries(registers)
            .filter(([, reg]) => !reg.isDeleted)
            .sort(([, a], [, b]) =>
                a.timestamp - b.timestamp || a.peerId.localeCompare(b.peerId)
            );

        this.remoteApplyDepth++;
        try {
            for (const [path, reg] of entries) {
                this._applyPath(path, reg.value, /*isDelete*/ false);
            }
        } finally {
            this.remoteApplyDepth--;
        }
    }

    _applyRemoteBatch(opBatch) {
        const ops = opBatch.operations || [];
        this.remoteApplyDepth++;
        try {
            for (const op of ops) {
                this._applyPath(op.path, op.value, op.$type === 'del');
            }
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
            // Whole-object set or delete.
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
                // Unhandled sub-path — quietly ignore for the demo.
                return;
        }
    }
}
