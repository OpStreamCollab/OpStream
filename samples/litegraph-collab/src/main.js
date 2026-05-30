import { CollabSession } from './collab-session.js';

// LiteGraph is loaded as a global from the CDN <script> in index.html.
const LG = window.LiteGraph;

const canvasEl = document.getElementById('mycanvas');
function resize() {
    canvasEl.width = window.innerWidth;
    canvasEl.height = window.innerHeight;
}
resize();

const graph = new LG.LGraph();
const canvas = new LG.LGraphCanvas('#mycanvas', graph);

window.addEventListener('resize', () => { resize(); canvas.resize(); });

const session = new CollabSession({
    url: '/collab',
    documentId: 'graph-demo',
    graph,
    canvas,
    onStatus: (s) => { const el = document.getElementById('status'); if (el) el.textContent = s; },
});

// Minimal toolbar — built-in node types ship with the litegraph dist.
const addNode = (type, x) => {
    const n = LG.createNode(type);
    if (!n) return;
    n.pos = [x + Math.random() * 80, 120 + Math.random() * 220];
    graph.add(n);
};
document.getElementById('addConst').onclick = () => {
    const n = LG.createNode('basic/const');
    n.pos = [120 + Math.random() * 80, 120 + Math.random() * 220];
    graph.add(n);
    if (n.setValue) n.setValue(Math.round(Math.random() * 10));
};
document.getElementById('addWatch').onclick = () => addNode('basic/watch', 460);

// Connect (seeds from the snapshot), then start the graph loop.
session.connect().then(() => graph.start()).catch((err) => console.error('[collab]', err));

window.__collab = session;
