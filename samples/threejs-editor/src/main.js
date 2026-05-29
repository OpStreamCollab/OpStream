import { CollabSession } from './collab-session.js';

const frame = document.getElementById('editorFrame');

// Wait for the editor iframe to finish bootstrapping (it sets window.editor
// inside its own <script type="module">). We poll briefly because there is no
// load signal we can rely on for "the editor instance is ready".
const waitForEditor = (win) => new Promise((resolve, reject) => {
    const deadline = Date.now() + 10000;
    const tick = () => {
        if (win.editor && win.editor.history) return resolve(win.editor);
        if (Date.now() > deadline) return reject(new Error('Editor not ready'));
        setTimeout(tick, 50);
    };
    tick();
});

frame.addEventListener('load', async () => {
    try {
        const editor = await waitForEditor(frame.contentWindow);
        const session = new CollabSession({
            url: '/collab',
            documentId: 'scene-demo',
            editor,
            editorWindow: frame.contentWindow,
        });
        await session.connect();
        window.__collab = session;
    } catch (err) {
        console.error(err);
    }
});
