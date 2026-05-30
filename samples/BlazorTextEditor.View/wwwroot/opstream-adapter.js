// opstream-adapter.js
window.OpStreamAdapter = {
    init: function (elementId, dotnetHelper) {
        const editor = document.getElementById(elementId);
        if (!editor) return;

        // Counter, not bool: nested/overlapping remote applies don't drop local input.
        let remoteApplyDepth = 0;
        let previousText = editor.value;

        // FIFO outbox. Diff ops carry unique information so we cannot coalesce
        // (unlike snapshot-based protocols). We just serialize: at most one
        // OnLocalInput in flight, the rest queued — preserves order on the server.
        const outbox = [];
        let flushing = false;

        const flush = async () => {
            if (flushing) return;
            flushing = true;
            try {
                while (outbox.length > 0) {
                    const opJson = outbox.shift();
                    try {
                        await dotnetHelper.invokeMethodAsync('OnLocalInput', opJson);
                    } catch (err) {
                        console.error('[OpStreamAdapter] OnLocalInput failed:', err);
                        break;
                    }
                }
            } finally {
                flushing = false;
            }
        };

        editor.addEventListener('input', () => {
            if (remoteApplyDepth > 0) return;

            const newText = editor.value;
            const op = computeDiffOp(previousText, newText);
            previousText = newText;

            if (op.components.length > 0) {
                outbox.push(JSON.stringify(op));
                flush();
            }
        });

        // Store functions in the DOM element to be called from C#
        editor.OpStream = {
            applyRemoteOp: function (opJson) {
                remoteApplyDepth++;
                try {
                    const op = JSON.parse(opJson);

                    let cursor = editor.selectionStart;
                    let currentText = editor.value;
                    let newText = "";
                    let currentIndex = 0;

                    for (const component of op.components) {
                        if (component.type === "retain") {
                            newText += currentText.substring(currentIndex, currentIndex + component.count);
                            currentIndex += component.count;
                        }
                        else if (component.type === "insert") {
                            newText += component.text;
                            if (currentIndex <= cursor) cursor += component.text.length;
                        }
                        else if (component.type === "delete") {
                            if (currentIndex < cursor) cursor = Math.max(0, cursor - component.count);
                            currentIndex += component.count;
                        }
                    }
                    newText += currentText.substring(currentIndex);

                    editor.value = newText;
                    previousText = newText;
                    editor.setSelectionRange(cursor, cursor);
                } finally {
                    remoteApplyDepth--;
                }
            },
            setContent: function(content) {
                remoteApplyDepth++;
                try {
                    editor.value = content;
                    previousText = content;
                    editor.disabled = false;
                } finally {
                    remoteApplyDepth--;
                }
            }
        };

        function computeDiffOp(oldText, newText) {
            let start = 0;
            while (start < oldText.length && start < newText.length && oldText[start] === newText[start]) start++;

            let oldEnd = oldText.length - 1;
            let newEnd = newText.length - 1;
            while (oldEnd >= start && newEnd >= start && oldText[oldEnd] === newText[newEnd]) {
                oldEnd--; newEnd--;
            }

            const deletedCount = oldEnd - start + 1;
            const insertedText = newText.substring(start, newEnd + 1);

            const op = { components: [] };
            if (start > 0) op.components.push({ type: "retain", count: start });
            if (deletedCount > 0) op.components.push({ type: "delete", count: deletedCount });
            if (insertedText.length > 0) op.components.push({ type: "insert", text: insertedText });

            return op;
        }
    },

    // Called from C# when a remote operation arrives
    applyRemoteOp: function (elementId, opJson) {
        const editor = document.getElementById(elementId);
        if (editor && editor.OpStream) {
            editor.OpStream.applyRemoteOp(opJson);
        }
    },
    
    setContent: function (elementId, content) {
        const editor = document.getElementById(elementId);
        if (editor && editor.OpStream) {
            editor.OpStream.setContent(content);
        }
    }
};
