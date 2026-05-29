import { CollabSession } from './collab-session.js';

// Luckysheet is loaded as a global from the CDN <script> tags in index.html.
const luckysheet = window.luckysheet;

const session = new CollabSession({
    url: '/collab',
    documentId: 'sheet-demo',
    luckysheet,
    onStatus: (s) => { const el = document.getElementById('status'); if (el) el.textContent = s; },
});

luckysheet.create({
    container: 'luckysheet',
    showinfobar: false,
    lang: 'en',
    data: [{ name: 'Sheet1', index: 0, status: 1, order: 0, row: 50, column: 20, config: {} }],
    hook: {
        // User edited a cell → send it.
        cellUpdated(r, c, oldValue, newValue) {
            session.onLocalEdit(r, c, newValue);
        },
        // Workbook is ready → safe to connect and seed the snapshot.
        workbookCreateAfter() {
            session.connect().catch((err) => console.error('[collab] connect failed', err));
        },
    },
});

window.__collab = session;
