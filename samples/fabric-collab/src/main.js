import { CollabSession } from './collab-session.js';

// fabric is a global from the CDN <script> in index.html.
const fabric = window.fabric;

const canvas = new fabric.Canvas('c', { backgroundColor: '#ffffff' });
function resize() {
    canvas.setWidth(window.innerWidth);
    canvas.setHeight(window.innerHeight - 48);
    canvas.renderAll();
}
resize();
window.addEventListener('resize', resize);

const session = new CollabSession({
    url: '/collab',
    documentId: 'canvas-demo',
    canvas,
    fabric,
    onStatus: (s) => { const el = document.getElementById('status'); if (el) el.textContent = s; },
});

const COLORS = ['#e91e63', '#3f51b5', '#009688', '#ff9800', '#9c27b0'];
const rnd = (n) => Math.random() * n;
const color = () => COLORS[Math.floor(Math.random() * COLORS.length)];

document.getElementById('addRect').onclick = () => {
    canvas.add(new fabric.Rect({
        left: 60 + rnd(300), top: 60 + rnd(200), width: 120, height: 80, fill: color(),
    }));
};
document.getElementById('addCircle').onclick = () => {
    canvas.add(new fabric.Circle({
        left: 60 + rnd(300), top: 60 + rnd(200), radius: 45, fill: color(),
    }));
};
document.getElementById('addText').onclick = () => {
    canvas.add(new fabric.Textbox('Edit me', {
        left: 60 + rnd(300), top: 60 + rnd(200), width: 160, fontSize: 24, fill: '#1a2230',
    }));
};
document.getElementById('del').onclick = () => {
    const active = canvas.getActiveObjects();
    active.forEach((o) => canvas.remove(o));
    canvas.discardActiveObject();
    canvas.requestRenderAll();
};

session.connect().catch((err) => console.error('[collab]', err));
window.__collab = session;
