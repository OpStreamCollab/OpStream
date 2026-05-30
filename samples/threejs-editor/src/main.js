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

// Oculta el panel lateral (#sidebar) del editor three.js y estira el viewport.
// El editor es same-origin, así que inyectamos un <style> en su documento y
// disparamos un 'resize' para que el renderer se reajuste. Pensado para grabar la
// demo de colaboración sin que el sidebar ocupe media pantalla.
const setupSidebarToggle = (frame) => {
    const doc = frame.contentWindow.document;
    const btn = document.getElementById('toggleSidebar');
    const STYLE_ID = 'opstream-hide-sidebar';
    let hidden = true; // oculto por defecto

    const apply = () => {
        let st = doc.getElementById(STYLE_ID);
        if (!st) { st = doc.createElement('style'); st.id = STYLE_ID; doc.head.appendChild(st); }
        st.textContent = hidden
            ? '#sidebar{display:none!important;} #viewport{right:0!important;}'
            : '';
        frame.contentWindow.dispatchEvent(new Event('resize')); // re-fit del renderer
        if (btn) btn.textContent = hidden ? 'Mostrar panel' : 'Ocultar panel';
    };

    if (btn) {
        btn.hidden = false;
        btn.addEventListener('click', () => { hidden = !hidden; apply(); });
    }
    apply();
};

frame.addEventListener('load', async () => {
    try {
        const editor = await waitForEditor(frame.contentWindow);
        setupSidebarToggle(frame);
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
