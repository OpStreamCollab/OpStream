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
            attached: false
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

    dispose: function (instanceId) {
        const instance = this._instances[instanceId];
        if (instance) {
            if (instance.observer) {
                instance.observer.disconnect();
            }
            delete this._instances[instanceId];
        }
    }
};
