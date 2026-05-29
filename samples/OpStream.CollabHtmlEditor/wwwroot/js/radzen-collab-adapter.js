// radzen-collab-adapter.js
// Bridges any contenteditable-based rich editor with the OpStream collaboration
// pipeline via Blazor interop. Editor-agnostic: the editable region is located
// through a configurable `contentSelector` (e.g. ".rz-html-editor-content" for
// Radzen, ".ql-editor" for Quill, "[contenteditable]" for a plain div).
window.RadzenCollabAdapter = {

    _instances: {},

    init: function (instanceId, containerSelector, contentSelector, dotnetHelper) {
        const instance = {
            dotnetHelper: dotnetHelper,
            containerSelector: containerSelector,
            contentSelector: contentSelector || '[contenteditable]',
            // Counter so nested/overlapping remote applies don't drop local input.
            remoteApplyDepth: 0,
            lastHtml: '',
            // Latest-wins coalescing: at most one OnLocalHtmlChange in flight,
            // at most one pending snapshot — newer keystrokes overwrite older ones.
            pendingHtml: null,
            flushing: false,
            // Set before the editable element is mounted; applied on attach.
            pendingInitialHtml: null,
            attached: false,
            // ── Presence / remote cursors ──────────────────────────────
            remotePeers: {},      // peerId -> { name, color, cursor }
            cursorEls: {},        // peerId -> DOM node (caret+label)
            cursorLayer: null,    // overlay element covering the editor
            cursorParent: null,   // positioned ancestor hosting the layer
            selectionDebounce: null,
            lastLocalCursor: -1
        };

        this._instances[instanceId] = instance;
        this._waitForEditor(instanceId, 50);
    },

    _waitForEditor: function (instanceId, attempts) {
        const instance = this._instances[instanceId];
        if (!instance) return;

        const container = document.querySelector(instance.containerSelector);
        const editorContent = container
            ? container.querySelector(instance.contentSelector)
            : null;

        if (editorContent) {
            this._attachListener(instanceId, editorContent);
        } else if (attempts > 0) {
            setTimeout(() => this._waitForEditor(instanceId, attempts - 1), 100);
        } else {
            console.warn('[RadzenCollabAdapter] Could not find editor element for:',
                instance.containerSelector, instance.contentSelector);
        }
    },

    _attachListener: function (instanceId, editorContent) {
        const instance = this._instances[instanceId];
        if (!instance) return;

        instance.editorElement = editorContent;
        instance.attached = true;

        // Seed any snapshot that arrived before the element was mounted.
        if (instance.pendingInitialHtml !== null) {
            this._applyHtml(instance, instance.pendingInitialHtml, null);
            instance.pendingInitialHtml = null;
        } else {
            instance.lastHtml = editorContent.innerHTML || '';
        }

        const onLocalChange = () => {
            if (instance.remoteApplyDepth > 0) return;
            const newHtml = editorContent.innerHTML || '';
            if (newHtml === instance.lastHtml) return;
            instance.lastHtml = newHtml;
            instance.pendingHtml = newHtml;
            RadzenCollabAdapter._flush(instance);
        };

        // `input` covers typing, paste, IME, and toolbar shortcuts.
        editorContent.addEventListener('input', onLocalChange);

        // MutationObserver catches programmatic DOM changes from the toolbar.
        const observer = new MutationObserver(onLocalChange);
        observer.observe(editorContent, {
            childList: true,
            subtree: true,
            characterData: true,
            attributes: true
        });
        instance.observer = observer;

        // ── Presence: report the local caret position so peers can render it ──
        instance.onSelectionChange = () => {
            if (instance.remoteApplyDepth > 0) return;
            const sel = window.getSelection();
            if (!sel || sel.rangeCount === 0) return;
            if (!editorContent.contains(sel.getRangeAt(0).startContainer)) return;
            clearTimeout(instance.selectionDebounce);
            instance.selectionDebounce = setTimeout(() => {
                const offset = RadzenCollabAdapter._getCursorCharOffset(editorContent);
                if (offset === null || offset === instance.lastLocalCursor) return;
                instance.lastLocalCursor = offset;
                instance.dotnetHelper.invokeMethodAsync('OnLocalCursorChange', offset)
                    .catch(err => console.error('[RadzenCollabAdapter] OnLocalCursorChange failed:', err));
            }, 80);
        };
        document.addEventListener('selectionchange', instance.onSelectionChange);

        // Remote carets are positioned in viewport-relative coordinates, so they
        // must be recomputed whenever the editor scrolls or the window resizes.
        instance.onReposition = () => RadzenCollabAdapter._renderAllRemote(instance);
        editorContent.addEventListener('scroll', instance.onReposition);
        window.addEventListener('resize', instance.onReposition);

        // Render any peers whose presence arrived before the editor mounted.
        this._renderAllRemote(instance);
    },

    _flush: async function (instance) {
        if (instance.flushing) return;
        instance.flushing = true;
        try {
            while (instance.pendingHtml !== null) {
                const html = instance.pendingHtml;
                instance.pendingHtml = null;
                try {
                    await instance.dotnetHelper.invokeMethodAsync('OnLocalHtmlChange', html);
                } catch (err) {
                    console.error('[RadzenCollabAdapter] OnLocalHtmlChange failed:', err);
                    break;
                }
            }
        } finally {
            instance.flushing = false;
        }
    },

    /**
     * Seeds the editor with the initial document snapshot. Safe to call before
     * the editable element is mounted — the value is stored and applied on attach.
     */
    setInitialHtml: function (instanceId, html) {
        const instance = this._instances[instanceId];
        if (!instance) return;
        if (instance.attached && instance.editorElement) {
            this._applyHtml(instance, html || '', null);
        } else {
            instance.pendingInitialHtml = html || '';
        }
    },

    /**
     * Applies a remote op: swaps the HTML and transforms the local cursor against
     * the op's components so the caret survives concurrent edits.
     */
    applyRemoteOp: function (instanceId, newHtml, ops) {
        const instance = this._instances[instanceId];
        if (!instance || !instance.editorElement) return;
        this._applyHtml(instance, newHtml, ops || null);
    },

    _applyHtml: function (instance, html, ops) {
        instance.remoteApplyDepth++;
        try {
            const el = instance.editorElement;
            const savedOffset = this._getCursorCharOffset(el);
            const hadSelection = savedOffset !== null;
            const newOffset = hadSelection ? this._transformCursor(savedOffset, ops) : null;

            el.innerHTML = html;
            if (hadSelection) {
                this._setCursorCharOffset(el, newOffset);
            }
            // Store the browser-normalized form so the next MutationObserver
            // comparison is apples-to-apples and doesn't re-send the op.
            instance.lastHtml = el.innerHTML;
            // Content shifted — reposition remote carets against the new text.
            RadzenCollabAdapter._renderAllRemote(instance);
        } finally {
            // MutationObserver callbacks are microtasks queued during the mutations
            // above and fire after this block. Defer one tick so they see depth > 0.
            Promise.resolve().then(() => { instance.remoteApplyDepth--; });
        }
    },

    _transformCursor: function (cursor, ops) {
        if (!ops || !ops.length) return cursor;
        let pos = 0;
        let out = cursor;
        for (const op of ops) {
            const type = (op.type || op.$type || '').toLowerCase();
            if (type === 'retain') {
                pos += op.count || op.Count || 0;
            } else if (type === 'insert') {
                const len = (op.text || op.Text || '').length;
                if (pos <= out) out += len;
            } else if (type === 'delete') {
                const cnt = op.count || op.Count || 0;
                if (pos < out) {
                    out -= Math.min(cnt, out - pos);
                }
                pos += cnt;
            }
        }
        return Math.max(0, out);
    },

    _getCursorCharOffset: function (root) {
        const selection = window.getSelection();
        if (!selection || selection.rangeCount === 0) return null;
        const range = selection.getRangeAt(0);
        if (!root.contains(range.startContainer)) return null;

        if (range.startContainer.nodeType === Node.ELEMENT_NODE) {
            const container = range.startContainer;
            let offset = 0;
            for (let i = 0; i < range.startOffset && i < container.childNodes.length; i++) {
                offset += container.childNodes[i].textContent.length;
            }
            return this._textOffsetBefore(root, container) + offset;
        }

        return this._textOffsetBefore(root, range.startContainer) + range.startOffset;
    },

    _textOffsetBefore: function (root, target) {
        const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, null);
        let offset = 0;
        while (walker.nextNode()) {
            const node = walker.currentNode;
            if (node === target) return offset;
            if (node.compareDocumentPosition(target) & Node.DOCUMENT_POSITION_PRECEDING) {
                return offset;
            }
            offset += node.textContent.length;
        }
        return offset;
    },

    _setCursorCharOffset: function (root, charOffset) {
        const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, null);
        let remaining = Math.max(0, charOffset);
        let target = null;
        let targetOff = 0;
        while (walker.nextNode()) {
            const node = walker.currentNode;
            const len = node.textContent.length;
            if (remaining <= len) {
                target = node;
                targetOff = remaining;
                break;
            }
            remaining -= len;
        }

        const sel = window.getSelection();
        const range = document.createRange();
        if (target) {
            range.setStart(target, targetOff);
            range.collapse(true);
        } else {
            range.selectNodeContents(root);
            range.collapse(false);
        }
        sel.removeAllRanges();
        sel.addRange(range);
    },

    syncLastHtml: function (instanceId, html) {
        const instance = this._instances[instanceId];
        if (!instance) return;
        instance.lastHtml = html;
    },

    // ─── Presence: remote cursor rendering ──────────────────────────────────

    /** Creates/updates a remote peer's caret + name label. */
    renderRemoteCursor: function (instanceId, peerId, name, color, cursor) {
        const instance = this._instances[instanceId];
        if (!instance) return;
        // Store regardless of attach state so peers present at join time render
        // once the editable element mounts (see _attachListener).
        instance.remotePeers[peerId] = {
            name: name || 'Anonymous',
            color: color || '#888',
            cursor: typeof cursor === 'number' ? cursor : 0
        };
        if (instance.editorElement) this._renderOneRemote(instance, peerId);
    },

    /** Removes a peer's caret (on disconnect). */
    removeRemoteCursor: function (instanceId, peerId) {
        const instance = this._instances[instanceId];
        if (!instance) return;
        delete instance.remotePeers[peerId];
        const node = instance.cursorEls[peerId];
        if (node) { node.remove(); delete instance.cursorEls[peerId]; }
    },

    _ensureLayer: function (instance) {
        if (instance.cursorLayer && instance.cursorLayer.isConnected) return instance.cursorLayer;
        const el = instance.editorElement;
        const parent = el.parentElement;
        if (!parent) return null;
        if (getComputedStyle(parent).position === 'static') parent.style.position = 'relative';
        const layer = document.createElement('div');
        layer.className = 'collab-cursor-layer';
        layer.style.cssText =
            'position:absolute; top:0; left:0; pointer-events:none; overflow:hidden; z-index:10;';
        parent.appendChild(layer);
        instance.cursorLayer = layer;
        instance.cursorParent = parent;
        return layer;
    },

    _positionLayer: function (instance) {
        const el = instance.editorElement;
        const parent = instance.cursorParent;
        const layer = instance.cursorLayer;
        if (!layer || !parent) return null;
        const elRect = el.getBoundingClientRect();
        const pRect = parent.getBoundingClientRect();
        layer.style.left = (elRect.left - pRect.left) + 'px';
        layer.style.top = (elRect.top - pRect.top) + 'px';
        layer.style.width = el.clientWidth + 'px';
        layer.style.height = el.clientHeight + 'px';
        return elRect;
    },

    _renderAllRemote: function (instance) {
        for (const peerId in instance.remotePeers) this._renderOneRemote(instance, peerId);
    },

    _renderOneRemote: function (instance, peerId) {
        const peer = instance.remotePeers[peerId];
        if (!peer) return;
        const layer = this._ensureLayer(instance);
        if (!layer) return;
        const elRect = this._positionLayer(instance);
        if (!elRect) return;

        const range = this._rangeForOffset(instance.editorElement, peer.cursor);
        const rect = range.getBoundingClientRect();

        let node = instance.cursorEls[peerId];
        if (!node) {
            node = document.createElement('div');
            node.className = 'collab-remote-cursor';
            node.style.cssText = 'position:absolute; pointer-events:none; transition:left .08s, top .08s;';
            const caret = document.createElement('div');
            caret.className = 'collab-remote-caret';
            const label = document.createElement('div');
            label.className = 'collab-remote-label';
            node.appendChild(caret);
            node.appendChild(label);
            layer.appendChild(node);
            instance.cursorEls[peerId] = node;
        }

        const caretEl = node.firstChild;
        const labelEl = node.lastChild;
        const height = rect.height || 18;
        const top = rect.top - elRect.top;
        caretEl.style.cssText =
            `width:2px; height:${height}px; background:${peer.color};`;
        labelEl.textContent = peer.name;
        // Flip the label below the caret when there's no room above (first line),
        // otherwise it would be clipped by the layer's overflow:hidden.
        const labelVertical = top < 18 ? `top:${height}px; border-radius:0 3px 3px 3px;`
                                       : 'top:-1.25em; border-radius:3px 3px 3px 0;';
        labelEl.style.cssText =
            `position:absolute; left:0; ${labelVertical} white-space:nowrap; font-size:.7rem; ` +
            `font-family:system-ui,sans-serif; font-weight:600; line-height:1.4; padding:1px 6px; ` +
            `color:#fff; background:${peer.color}; box-shadow:0 1px 4px rgba(0,0,0,.35);`;

        node.style.left = (rect.left - elRect.left) + 'px';
        node.style.top = top + 'px';
    },

    _rangeForOffset: function (root, charOffset) {
        const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, null);
        let remaining = Math.max(0, charOffset);
        let target = null, targetOff = 0;
        while (walker.nextNode()) {
            const node = walker.currentNode;
            const len = node.textContent.length;
            if (remaining <= len) { target = node; targetOff = remaining; break; }
            remaining -= len;
        }
        const range = document.createRange();
        if (target) { range.setStart(target, targetOff); range.collapse(true); }
        else { range.selectNodeContents(root); range.collapse(false); }
        return range;
    },

    dispose: function (instanceId) {
        const instance = this._instances[instanceId];
        if (instance) {
            if (instance.observer) {
                instance.observer.disconnect();
            }
            if (instance.onSelectionChange) {
                document.removeEventListener('selectionchange', instance.onSelectionChange);
            }
            if (instance.onReposition) {
                window.removeEventListener('resize', instance.onReposition);
                if (instance.editorElement) {
                    instance.editorElement.removeEventListener('scroll', instance.onReposition);
                }
            }
            if (instance.cursorLayer) instance.cursorLayer.remove();
            delete this._instances[instanceId];
        }
    }
};
