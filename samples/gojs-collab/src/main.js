import { CollabSession } from './collab-session.js';

const go = window.go; // global from the CDN <script>
const $ = go.GraphObject.make;

const uuid = () => 'k-' + Math.random().toString(36).slice(2, 10);
const FILLS = ['#e3f2fd', '#fff3e0', '#e8f5e9', '#fce4ec', '#ede7f6', '#e0f7fa'];
const PALETTE = ['#e91e63', '#3f51b5', '#009688', '#ff9800', '#9c27b0', '#2d6cdf'];
const pick = (a) => a[Math.floor(Math.random() * a.length)];

const diagram = $(go.Diagram, 'diagram', {
    'undoManager.isEnabled': true,
    layout: $(go.Layout), // free placement
});

// ── Node templates, one per `category` (the data carries `category`, so the
//    shape syncs automatically through the JSON register). ───────────────────
const textBlock = () => $(go.TextBlock,
    { margin: 8, editable: true, font: '14px system-ui', stroke: '#1a2230',
      maxSize: new go.Size(160, NaN), textAlign: 'center' },
    new go.Binding('text').makeTwoWay());

// 'Parallelogram1' / 'Capsule' live in GoJS' Figures.js extension (not in the
// base build) → define the parallelogram ourselves (lines only) and use a very
// round RoundedRectangle for the start/end terminator.
go.Shape.defineFigureGenerator('Parallelogram1', (shape, w, h) => {
    const skew = Math.min(w * 0.22, h);
    const geo = new go.Geometry();
    const fig = new go.PathFigure(skew, 0, true);
    geo.add(fig);
    fig.add(new go.PathSegment(go.PathSegment.Line, w, 0));
    fig.add(new go.PathSegment(go.PathSegment.Line, w - skew, h));
    fig.add(new go.PathSegment(go.PathSegment.Line, 0, h));
    fig.add(new go.PathSegment(go.PathSegment.Line, skew, 0).close());
    return geo;
});

const baseNode = (figure, nodeOpts = {}, shapeOpts = {}) => $(go.Node, 'Auto',
    { locationSpot: go.Spot.Center, fromLinkable: true, toLinkable: true,
      fromLinkableSelfNode: false, toLinkableSelfNode: false,
      resizable: false, ...nodeOpts },
    new go.Binding('location', 'loc', go.Point.parse).makeTwoWay(go.Point.stringify),
    $(go.Shape, figure,
        { strokeWidth: 2, stroke: '#5a6678', portId: '', cursor: 'pointer',
          fromSpot: go.Spot.AllSides, toSpot: go.Spot.AllSides, ...shapeOpts },
        new go.Binding('fill', 'color')),
    textBlock());

diagram.nodeTemplateMap.add('', baseNode('RoundedRectangle'));        // process (default)
diagram.nodeTemplateMap.add('process', baseNode('RoundedRectangle'));
diagram.nodeTemplateMap.add('decision', baseNode('Diamond', { desiredSize: new go.Size(120, 90) }));
diagram.nodeTemplateMap.add('start', baseNode('RoundedRectangle', {}, { parameter1: 20 })); // terminator
diagram.nodeTemplateMap.add('data', baseNode('Parallelogram1'));     // I/O

diagram.linkTemplate = $(go.Link,
    { relinkableFrom: true, relinkableTo: true, reshapable: true, corner: 6 },
    $(go.Shape, { strokeWidth: 2, stroke: '#5a6678' }),
    $(go.Shape, { toArrow: 'StretchedDiamond', stroke: '#5a6678', fill: '#5a6678' }),
);

const model = new go.GraphLinksModel();
model.linkKeyProperty = 'key';
model.makeUniqueKeyFunction = () => uuid();
model.makeUniqueLinkKeyFunction = () => uuid();
diagram.model = model;

// ── Presence: a random name + color for this user ────────────────────────────
const presence = {
    name: 'User-' + Math.floor(100 + Math.random() * 900),
    color: pick(PALETTE),
};
const meEl = document.getElementById('me');
meEl.textContent = '● ' + presence.name;
meEl.style.color = presence.color;

let comments = [];
let selectedKey = null;
let pins = {};      // nodeKey -> pin element
let flashes = [];   // { key, el, until }

const session = new CollabSession({
    url: '/collab', documentId: 'flow-demo', diagram, presence,
    onStatus: (s) => { const el = document.getElementById('status'); if (el) el.textContent = s; },
    onPeers: renderPeers,
    onRemoteEdit: flashEdit,
    onComments: (list) => { comments = list; renderPins(); renderPanel(); },
});

// ── Toolbar: add the different node types ────────────────────────────────────
let drop = 0;
function addNode(category, text) {
    const p = diagram.transformViewToDoc(new go.Point(90 + (drop % 5) * 40, 90 + (drop % 7) * 36));
    drop++;
    diagram.model.commit((m) => {
        const data = { category, text, color: pick(FILLS), loc: go.Point.stringify(p) };
        m.addNodeData(data);
        const node = diagram.findNodeForData(data);
        if (node) diagram.select(node);
    }, 'add ' + (category || 'process'));
}
document.getElementById('addProcess').onclick = () => addNode('process', 'Step');
document.getElementById('addDecision').onclick = () => addNode('decision', 'Choice?');
document.getElementById('addStart').onclick = () => addNode('start', 'Start');
document.getElementById('addData').onclick = () => addNode('data', 'Data');
document.getElementById('del').onclick = () => diagram.commandHandler.deleteSelection();

// ── Selection drives the comment box ─────────────────────────────────────────
diagram.addDiagramListener('ChangedSelection', () => {
    const n = diagram.selection.first();
    selectedKey = (n instanceof go.Node && n.data) ? n.data.key : null;
    renderPanel();
});

document.getElementById('cmAdd').onclick = async () => {
    const ta = document.getElementById('cmBody');
    const body = ta.value.trim();
    if (!body || !selectedKey) return;
    ta.value = '';
    try { await session.addComment(selectedKey, body); }
    catch (e) { console.error('addComment', e); }
};

session.connect().catch((e) => console.error('[collab]', e));
window.__collab = session;

// ── Anchored overlay: keep pins + edit flashes glued to their nodes ──────────
const overlay = document.getElementById('overlay');

function nodeScreenRect(key) {
    const node = diagram.findNodeForKey(key);
    if (!node) return null;
    const b = node.actualBounds; // document coords
    const div = diagram.div.getBoundingClientRect();
    const tl = diagram.transformDocToView(new go.Point(b.x, b.y));
    const br = diagram.transformDocToView(new go.Point(b.x + b.width, b.y + b.height));
    return { left: div.left + tl.x, top: div.top + tl.y,
             right: div.left + br.x, bottom: div.top + br.y };
}

function tick() {
    // pins → top-right corner of the node
    for (const key of Object.keys(pins)) {
        const el = pins[key];
        const r = nodeScreenRect(key);
        if (!r) { el.style.display = 'none'; continue; }
        el.style.display = '';
        el.style.left = (r.right - 6) + 'px';
        el.style.top = (r.top - 8) + 'px';
    }
    // edit flashes → above the node
    const now = Date.now();
    flashes = flashes.filter((f) => {
        if (now > f.until) { f.el.remove(); return false; }
        const r = nodeScreenRect(f.key);
        if (!r) { f.el.style.display = 'none'; return true; }
        f.el.style.display = '';
        f.el.style.left = r.left + 'px';
        f.el.style.top = (r.top - 24) + 'px';
        return true;
    });
    requestAnimationFrame(tick);
}
requestAnimationFrame(tick);

// ── Remote-edit feedback: colored label + glow on the touched node ───────────
function flashEdit(nodeKey, peer, kind) {
    if (kind === 'del') return;
    const el = document.createElement('div');
    el.className = 'flash';
    el.style.background = peer.color;
    el.textContent = peer.name;
    overlay.appendChild(el);
    flashes.push({ key: nodeKey, el, until: Date.now() + 2600 });
    // brief colored glow on the node's main shape
    const node = diagram.findNodeForKey(nodeKey);
    const target = node && node.findMainElement && node.findMainElement();
    if (target) {
        const prev = target.stroke, prevW = target.strokeWidth;
        target.stroke = peer.color; target.strokeWidth = 4;
        setTimeout(() => { target.stroke = prev; target.strokeWidth = prevW; }, 2600);
    }
}

// ── Presence list ────────────────────────────────────────────────────────────
function renderPeers(peers) {
    const el = document.getElementById('peers');
    const others = peers.filter((p) => p.name !== presence.name);
    const dot = (c) => `<span class="dot" style="background:${c}"></span>`;
    el.innerHTML = `<div class="peer">${dot(presence.color)}${presence.name} <span class="you">(you)</span></div>`
        + others.map((p) => `<div class="peer">${dot(p.color)}${p.name}</div>`).join('');
    // New presence may resolve authors of already-rendered comments → re-render.
    renderPanel();
}

// ── Comment pins (one per node with open comments) ───────────────────────────
function renderPins() {
    overlay.querySelectorAll('.pin').forEach((e) => e.remove());
    pins = {};
    const byKey = {};
    for (const c of comments) {
        if (!c.nodeKey) continue;
        (byKey[c.nodeKey] = byKey[c.nodeKey] || []).push(c);
    }
    for (const key of Object.keys(byKey)) {
        const pin = document.createElement('div');
        pin.className = 'pin';
        pin.textContent = '💬 ' + byKey[key].length;
        pin.onclick = () => selectNode(key);
        overlay.appendChild(pin);
        pins[key] = pin;
    }
}

function selectNode(key) {
    const node = diagram.findNodeForKey(key);
    if (!node) return;
    diagram.select(node);
    diagram.commandHandler.scrollToPart(node);
    selectedKey = key;
    renderPanel();
}

// ── Comment panel ────────────────────────────────────────────────────────────
function renderPanel() {
    const panel = document.getElementById('panel');
    const head = document.getElementById('cmHead');
    const addBtn = document.getElementById('cmAdd');

    addBtn.disabled = !selectedKey;
    addBtn.textContent = selectedKey ? 'Comment on selected' : 'Select a node first';

    const shown = selectedKey ? comments.filter((c) => c.nodeKey === selectedKey) : comments;
    head.textContent = selectedKey ? 'Comments · selected node' : `Comments · all (${comments.length})`;

    if (!shown.length) {
        panel.innerHTML = `<p class="empty">No open comments${selectedKey ? ' on this node' : ''}.</p>`;
        return;
    }
    panel.innerHTML = '';
    for (const c of shown) {
        const author = session.peerByConn(c.authorPeerId);
        const name = author ? author.name : 'Someone';
        const color = author ? author.color : '#9aa3ad';

        const row = document.createElement('div');
        row.className = 'cm';
        row.innerHTML =
            `<div class="body"></div>` +
            `<div class="meta"><span class="dot" style="background:${color}"></span>` +
            `<span class="who"></span>${!selectedKey ? ' · <span class="lnk">go to node</span>' : ''}</div>`;
        row.querySelector('.body').textContent = c.body;
        row.querySelector('.who').textContent = name;

        const res = document.createElement('button');
        res.className = 'resolve';
        res.title = 'Resolve';
        res.textContent = '✓';
        res.onclick = () => session.resolveComment(c.id).catch((e) => console.error(e));
        row.appendChild(res);

        const lnk = row.querySelector('.lnk');
        if (lnk) lnk.onclick = () => selectNode(c.nodeKey);
        panel.appendChild(row);
    }
}
