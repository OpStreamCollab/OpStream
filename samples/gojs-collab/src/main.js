import { CollabSession } from './collab-session.js';

// go is a global from the CDN <script> in index.html.
const go = window.go;
const $ = go.GraphObject.make;

const uuid = () => 'k-' + Math.random().toString(36).slice(2, 10);
const COLORS = ['#e3f2fd', '#fff3e0', '#e8f5e9', '#fce4ec', '#ede7f6'];

const diagram = $(go.Diagram, 'diagram', {
    'undoManager.isEnabled': true,
    layout: $(go.Layout), // no auto-layout; users place nodes freely
});

// Node: rounded box with editable text, position bound two-way to data.loc.
diagram.nodeTemplate = $(go.Node, 'Auto',
    { locationSpot: go.Spot.Center, fromLinkable: true, toLinkable: true,
      fromLinkableSelfNode: false, toLinkableSelfNode: false },
    new go.Binding('location', 'loc', go.Point.parse).makeTwoWay(go.Point.stringify),
    $(go.Shape, 'RoundedRectangle',
        { strokeWidth: 2, stroke: '#5a6678', portId: '', cursor: 'pointer' },
        new go.Binding('fill', 'color')),
    $(go.TextBlock, { margin: 10, editable: true, font: '14px system-ui' },
        new go.Binding('text').makeTwoWay()),
);

diagram.linkTemplate = $(go.Link,
    { relinkableFrom: true, relinkableTo: true, reshapable: true },
    $(go.Shape, { strokeWidth: 2, stroke: '#5a6678' }),
    $(go.Shape, { toArrow: 'Standard', stroke: '#5a6678', fill: '#5a6678' }),
);

// String uuid keys so nodes/links are globally unique across peers.
const model = new go.GraphLinksModel();
model.linkKeyProperty = 'key';
model.makeUniqueKeyFunction = () => uuid();
model.makeUniqueLinkKeyFunction = () => uuid();
diagram.model = model;

const session = new CollabSession({
    url: '/collab',
    documentId: 'flow-demo',
    diagram,
    onStatus: (s) => { const el = document.getElementById('status'); if (el) el.textContent = s; },
});

let dropX = 40;
document.getElementById('addNode').onclick = () => {
    const p = diagram.transformViewToDoc(new go.Point(80 + (dropX % 360), 80 + (dropX % 200)));
    dropX += 70;
    // Model.commit pasa el Model al callback (Diagram.commit pasaría el Diagram,
    // que no tiene addNodeData).
    diagram.model.commit((m) => {
        m.addNodeData({ text: 'Step', color: COLORS[Math.floor(Math.random() * COLORS.length)],
            loc: go.Point.stringify(p) });
    }, 'add node');
};

session.connect().catch((err) => console.error('[collab]', err));
window.__collab = session;
