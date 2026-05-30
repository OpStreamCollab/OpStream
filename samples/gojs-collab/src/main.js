import { CollabSession } from './collab-session.js';

const go = window.go; // global from the CDN <script>
const $ = go.GraphObject.make;

const uuid = () => 'k-' + Math.random().toString(36).slice(2, 10);
const PRESENCE_PALETTE = ['#e91e63', '#3f51b5', '#009688', '#ff9800', '#9c27b0', '#2d6cdf'];
const pick = (a) => a[Math.floor(Math.random() * a.length)];

// Fill palette offered in the toolbar for recoloring the selected node.
const NODE_COLORS = ['#3f7fff', '#1f9d6b', '#e0922f', '#19a3a3', '#c0392b', '#9c5bd6', '#d9b400'];

// ── Diagram ──────────────────────────────────────────────────────────────────
const diagram = $(go.Diagram, 'diagram', {
    'undoManager.isEnabled': true,
    'draggingTool.isGridSnapEnabled': true,
    'animationManager.isEnabled': false,
    initialContentAlignment: go.Spot.Center,
    layout: $(go.Layout), // free placement (palette-driven authoring)
    grid: $(go.Panel, 'Grid', { gridCellSize: new go.Size(20, 20) },
        $(go.Shape, 'LineH', { stroke: '#19264180', strokeWidth: 1 }),
        $(go.Shape, 'LineV', { stroke: '#19264180', strokeWidth: 1 })),
});

// ── Ports: 4 transparent stubs per node, shown on hover, link-drawable ────────
function makePort(id, spot, output, input) {
    return $(go.Shape, 'Circle', {
        fill: 'transparent', strokeWidth: 0, width: 11, height: 11,
        alignment: spot, alignmentFocus: spot,
        portId: id, fromSpot: spot, toSpot: spot,
        fromLinkable: output, toLinkable: input, cursor: 'crosshair',
    });
}
function showPorts(node, show) {
    node.ports.each((p) => {
        if (p.portId !== '') p.fill = show ? 'rgba(63,127,255,.85)' : 'transparent';
    });
}

// 'Parallelogram1' lives in GoJS' Figures.js extension (not in the base build) →
// define it ourselves so the I/O node renders without pulling extra scripts.
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

const textBlock = (stroke = '#ffffff') => $(go.TextBlock,
    { margin: 10, editable: true, font: '600 13px system-ui', stroke,
      maxSize: new go.Size(150, NaN), textAlign: 'center', wrap: go.TextBlock.WrapFit },
    new go.Binding('text').makeTwoWay());

// Generic linkable node (shape + label + 4 ports). `shapeOpts` lets a category
// tweak the geometry (e.g. a diamond's desired size or a terminator's roundness).
function flowNode(figure, shapeOpts = {}, textColor = '#fff') {
    return $(go.Node, 'Spot',
        { locationSpot: go.Spot.Center, selectionAdorned: true,
          resizable: false, isShadowed: true, shadowColor: 'rgba(0,0,0,.45)',
          shadowOffset: new go.Point(0, 2), shadowBlur: 6,
          mouseEnter: (e, node) => showPorts(node, true),
          mouseLeave: (e, node) => showPorts(node, false) },
        new go.Binding('location', 'loc', go.Point.parse).makeTwoWay(go.Point.stringify),
        $(go.Panel, 'Auto',
            $(go.Shape, figure,
                { strokeWidth: 1.5, stroke: 'rgba(255,255,255,.25)', portId: '',
                  fromLinkable: true, toLinkable: true,
                  fromSpot: go.Spot.AllSides, toSpot: go.Spot.AllSides,
                  cursor: 'pointer', ...shapeOpts },
                new go.Binding('fill', 'color')),
            textBlock(textColor)),
        makePort('T', go.Spot.Top, true, true),
        makePort('L', go.Spot.Left, true, true),
        makePort('R', go.Spot.Right, true, true),
        makePort('B', go.Spot.Bottom, true, true),
    );
}

diagram.nodeTemplateMap.add('process', flowNode('RoundedRectangle'));
diagram.nodeTemplateMap.add('', flowNode('RoundedRectangle')); // default
diagram.nodeTemplateMap.add('decision', flowNode('Diamond', { desiredSize: new go.Size(130, 95) }));
diagram.nodeTemplateMap.add('start', flowNode('Capsule', { parameter1: 18 }));
diagram.nodeTemplateMap.add('end', flowNode('Capsule', { parameter1: 18 }));
diagram.nodeTemplateMap.add('data', flowNode('Parallelogram1'));

// A non-linkable sticky annotation. Useful in a workflow, distinct from the
// OpStream comment threads in the sidebar.
diagram.nodeTemplateMap.add('note', $(go.Node, 'Auto',
    { locationSpot: go.Spot.Center, resizable: true, minSize: new go.Size(90, 50) },
    new go.Binding('location', 'loc', go.Point.parse).makeTwoWay(go.Point.stringify),
    $(go.Shape, 'RoundedRectangle',
        { strokeWidth: 0, fill: '#d9b400' },
        new go.Binding('fill', 'color')),
    $(go.TextBlock, { margin: 9, editable: true, font: '13px system-ui',
        stroke: '#2a2200', maxSize: new go.Size(170, NaN), wrap: go.TextBlock.WrapFit },
        new go.Binding('text').makeTwoWay())));

// ── Links: orthogonal, relinkable, reshapable, with an editable label ─────────
diagram.linkTemplate = $(go.Link,
    { routing: go.Link.AvoidsNodes, corner: 10, curve: go.Link.JumpOver,
      relinkableFrom: true, relinkableTo: true, reshapable: true, resegmentable: true,
      fromSpot: go.Spot.AllSides, toSpot: go.Spot.AllSides,
      selectionAdorned: true },
    $(go.Shape, { strokeWidth: 2, stroke: '#7088b8' }),
    $(go.Shape, { toArrow: 'Standard', strokeWidth: 0, fill: '#7088b8', scale: 1.3 }),
    $(go.Panel, 'Auto',
        { visible: false, segmentIndex: NaN, segmentFraction: 0.5 },
        new go.Binding('visible', 'text', (t) => !!t),
        $(go.Shape, 'RoundedRectangle', { fill: '#16223f', stroke: '#33446b', strokeWidth: 1 }),
        $(go.TextBlock, { editable: true, font: '600 11px system-ui', stroke: '#cdd6e4',
            margin: new go.Margin(2, 6) },
            new go.Binding('text').makeTwoWay())));

const model = new go.GraphLinksModel();
model.linkKeyProperty = 'key';
model.makeUniqueKeyFunction = () => uuid();
model.makeUniqueLinkKeyFunction = () => uuid();
diagram.model = model;

// Double-click a link to give it a label (then double-click the chip to edit).
diagram.addDiagramListener('ObjectDoubleClicked', (e) => {
    const part = e.subject && e.subject.part;
    if (part instanceof go.Link && !part.data.text) {
        diagram.model.commit((m) => m.set(part.data, 'text', 'Yes'), 'add label');
    }
});

// ── Palette (left pane): drag a prototype onto the canvas ─────────────────────
const palette = $(go.Palette, 'palette', {
    nodeTemplateMap: diagram.nodeTemplateMap,
    'animationManager.isEnabled': false,
    layout: $(go.GridLayout, { wrappingColumn: 1, cellSize: new go.Size(2, 2),
        spacing: new go.Size(8, 12) }),
});
palette.model = new go.GraphLinksModel([
    { category: 'start',    text: 'Start',   color: '#1f9d6b' },
    { category: 'process',  text: 'Step',    color: '#3f7fff' },
    { category: 'decision', text: 'Choice?', color: '#e0922f' },
    { category: 'data',     text: 'Data',    color: '#19a3a3' },
    { category: 'end',      text: 'End',     color: '#c0392b' },
    { category: 'note',     text: 'Note',    color: '#d9b400' },
]);

// ── Presence: a random name + color for this user ────────────────────────────
const presence = {
    name: 'User-' + Math.floor(100 + Math.random() * 900),
    color: pick(PRESENCE_PALETTE),
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

// ── Toolbar ───────────────────────────────────────────────────────────────────
const undoBtn = document.getElementById('undo');
const redoBtn = document.getElementById('redo');
undoBtn.onclick = () => diagram.commandHandler.undo();
redoBtn.onclick = () => diagram.commandHandler.redo();
document.getElementById('fit').onclick = () => diagram.commandHandler.zoomToFit();
document.getElementById('del').onclick = () => diagram.commandHandler.deleteSelection();

function syncUndoButtons() {
    const um = diagram.undoManager;
    undoBtn.disabled = !um.canUndo();
    redoBtn.disabled = !um.canRedo();
}
syncUndoButtons();

// Color swatches recolor the selected node(s).
const swatches = document.getElementById('swatches');
for (const c of NODE_COLORS) {
    const sw = document.createElement('span');
    sw.className = 'sw';
    sw.style.background = c;
    sw.title = c;
    sw.onclick = () => {
        const nodes = diagram.selection.toArray().filter((p) => p instanceof go.Node);
        if (!nodes.length) return;
        diagram.model.commit((m) => {
            for (const n of nodes) m.set(n.data, 'color', c);
        }, 'recolor');
    };
    swatches.appendChild(sw);
}

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
    syncUndoButtons();
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
