import { CollabSession } from './collab-session.js';

const fabric = window.fabric;
const SIDEBAR = 280;

const canvasEl = document.getElementById('c');
const canvas = new fabric.Canvas('c', { backgroundColor: '#ffffff' });

function resize() {
    canvas.setWidth(window.innerWidth - SIDEBAR);
    canvas.setHeight(window.innerHeight - 48);
    canvas.renderAll();
    repositionPins();
}
window.addEventListener('resize', resize);

// ── Presence: a random name + color for this user ────────────────────────────
const PALETTE = ['#e91e63', '#3f51b5', '#009688', '#ff9800', '#9c27b0', '#2d6cdf'];
const presence = {
    peerId: 'peer-' + Math.random().toString(36).slice(2, 8),
    name: 'User-' + Math.floor(100 + Math.random() * 900),
    color: PALETTE[Math.floor(Math.random() * PALETTE.length)],
};
const meEl = document.getElementById('me');
meEl.textContent = '● ' + presence.name;
meEl.style.color = presence.color;

let comments = [];
let selectedId = null;
let pinByObj = {};

const session = new CollabSession({
    url: '/collab', documentId: 'canvas-demo', canvas, fabric, presence,
    onStatus: (s) => { document.getElementById('status').textContent = s; },
    onPeers: renderPeers,
    onRemoteEdit: flashEdit,
    onComments: (list) => { comments = list; renderPins(); renderPanel(); },
});

// ── Toolbar ──────────────────────────────────────────────────────────────────
const rnd = (n) => Math.random() * n;
const color = () => PALETTE[Math.floor(Math.random() * PALETTE.length)];
document.getElementById('addRect').onclick = () =>
    canvas.add(new fabric.Rect({ left: 60 + rnd(280), top: 60 + rnd(200), width: 120, height: 80, fill: color() }));
document.getElementById('addCircle').onclick = () =>
    canvas.add(new fabric.Circle({ left: 60 + rnd(280), top: 60 + rnd(200), radius: 45, fill: color() }));
document.getElementById('addText').onclick = () =>
    canvas.add(new fabric.Textbox('Edit me', { left: 60 + rnd(280), top: 60 + rnd(200), width: 160, fontSize: 24, fill: '#1a2230' }));
document.getElementById('del').onclick = () => {
    canvas.getActiveObjects().forEach((o) => canvas.remove(o));
    canvas.discardActiveObject();
    canvas.requestRenderAll();
};

// ── Selection drives the comment box ─────────────────────────────────────────
canvas.on('selection:created', updateSel);
canvas.on('selection:updated', updateSel);
canvas.on('selection:cleared', () => { selectedId = null; renderPanel(); });
function updateSel() {
    const a = canvas.getActiveObject();
    selectedId = (a && a.id) ? a.id : null;
    renderPanel();
}
canvas.on('after:render', repositionPins);

document.getElementById('cmAdd').onclick = async () => {
    const body = document.getElementById('cmBody').value.trim();
    if (!body || !selectedId) return;
    document.getElementById('cmBody').value = '';
    try { await session.addComment(selectedId, body); }
    catch (e) { console.error('addComment', e); }
};

resize();
session.connect().catch((e) => console.error('[collab]', e));

// ── Remote-edit feedback: colored border + name label on the touched object ──
function flashEdit(objectId, peer, kind) {
    if (kind === 'del') return;
    const obj = canvas.getObjects().find((o) => o.id === objectId);
    if (!obj) return;
    const r = obj.getBoundingRect(true);
    const border = new fabric.Rect({
        left: r.left - 3, top: r.top - 3, width: r.width + 6, height: r.height + 6,
        fill: '', stroke: peer.color, strokeWidth: 2, strokeDashArray: [6, 4], rx: 4, ry: 4,
        selectable: false, evented: false, excludeFromExport: true,
    });
    const label = new fabric.Text(' ' + peer.name + ' ', {
        left: r.left - 3, top: r.top - 22, fontSize: 12, fontFamily: 'system-ui',
        fill: '#fff', backgroundColor: peer.color,
        selectable: false, evented: false, excludeFromExport: true,
    });
    canvas.add(border, label);
    canvas.requestRenderAll();
    setTimeout(() => { canvas.remove(border, label); canvas.requestRenderAll(); }, 2600);
}

// ── Presence list ────────────────────────────────────────────────────────────
function renderPeers(peers) {
    const el = document.getElementById('peers');
    const others = peers.filter((p) => p.name !== presence.name);
    el.innerHTML = `<span class="dot" style="background:${presence.color}"></span>${presence.name} (you)`
        + others.map((p) => `<span class="dot" style="background:${p.color}"></span>${p.name}`).join('');
}

// ── Comment pins anchored to objects (HTML overlay) ──────────────────────────
function renderPins() {
    const overlay = document.getElementById('overlay');
    overlay.innerHTML = '';
    pinByObj = {};
    const byObj = {};
    for (const c of comments) {
        const oid = CollabSession.objectIdOf(c);
        if (!oid) continue;
        (byObj[oid] = byObj[oid] || []).push(c);
    }
    for (const oid of Object.keys(byObj)) {
        const pin = document.createElement('div');
        pin.className = 'pin';
        pin.textContent = '💬 ' + byObj[oid].length;
        pin.onclick = () => selectObject(oid);
        overlay.appendChild(pin);
        pinByObj[oid] = pin;
    }
    repositionPins();
}

function repositionPins() {
    const cr = canvasEl.getBoundingClientRect();
    for (const oid of Object.keys(pinByObj)) {
        const obj = canvas.getObjects().find((o) => o.id === oid);
        const el = pinByObj[oid];
        if (!obj) { el.style.display = 'none'; continue; }
        const r = obj.getBoundingRect(true);
        el.style.display = '';
        el.style.left = (cr.left + r.left + r.width - 8) + 'px';
        el.style.top = (cr.top + r.top - 12) + 'px';
    }
}

function selectObject(oid) {
    const obj = canvas.getObjects().find((o) => o.id === oid);
    if (!obj) return;
    canvas.setActiveObject(obj);
    canvas.requestRenderAll();
    selectedId = oid;
    renderPanel();
}

// ── Comment panel ────────────────────────────────────────────────────────────
function renderPanel() {
    const panel = document.getElementById('panel');
    const head = document.getElementById('cmHead');
    const addBtn = document.getElementById('cmAdd');

    addBtn.disabled = !selectedId;
    addBtn.textContent = selectedId ? 'Comment on selected' : 'Select an object first';

    const shown = selectedId
        ? comments.filter((c) => CollabSession.objectIdOf(c) === selectedId)
        : comments;
    head.textContent = selectedId ? `Comments · selected object` : `Comments · all (${comments.length})`;

    if (!shown.length) { panel.innerHTML = `<p class="empty">No open comments${selectedId ? ' on this object' : ''}.</p>`; return; }
    panel.innerHTML = '';
    for (const c of shown) {
        const row = document.createElement('div');
        row.className = 'cm';
        const who = (c.authorId || '').slice(0, 6);
        row.innerHTML = `<div class="body"></div><div class="meta">by ${who}` +
            (!selectedId ? ` · on ${(CollabSession.objectIdOf(c) || '').slice(0, 8)}` : '') + `</div>`;
        row.querySelector('.body').textContent = c.body;
        const res = document.createElement('button');
        res.className = 'resolve';
        res.textContent = 'Resolve';
        res.onclick = () => session.resolveComment(c.id).catch((e) => console.error(e));
        row.appendChild(res);
        if (!selectedId) row.querySelector('.meta').onclick = () => selectObject(CollabSession.objectIdOf(c));
        panel.appendChild(row);
    }
}

window.__collab = session;
