import { CollabSession } from './collab-session.js';

const Konva = window.Konva;
const uuid  = () => 'k' + Math.random().toString(36).slice(2, 10);
const PALETTE = ['#e91e63','#3f51b5','#009688','#ff9800','#9c27b0','#2d6cdf','#f44336','#00bcd4'];
const pick = (a) => a[Math.floor(Math.random() * a.length)];

// ── Stage setup ───────────────────────────────────────────────────────────────
const TOOL_W    = 56;
const SIDEBAR_W = 280;
const TOP_H     = 46;

const stage = new Konva.Stage({
    container: 'container',
    width:  Math.max(100, window.innerWidth  - TOOL_W - SIDEBAR_W),
    height: Math.max(100, window.innerHeight - TOP_H),
});
const layer = new Konva.Layer();
stage.add(layer);

const tr = new Konva.Transformer({
    anchorSize: 8, rotateAnchorOffset: 26,
    borderStroke: '#2d6cdf', borderStrokeWidth: 1.5,
    anchorStroke: '#2d6cdf', anchorFill: '#fff', anchorCornerRadius: 2,
    boundBoxFunc: (old, nw) => (nw.width < 4 || nw.height < 4) ? old : nw,
});
layer.add(tr);
layer.batchDraw();

window.addEventListener('resize', () => {
    stage.width(Math.max(100, window.innerWidth  - TOOL_W - SIDEBAR_W));
    stage.height(Math.max(100, window.innerHeight - TOP_H));
    layer.batchDraw();
});

// ── Presence ──────────────────────────────────────────────────────────────────
const presence = { name: 'User-' + Math.floor(100 + Math.random() * 900), color: pick(PALETTE) };
const meEl = document.getElementById('me');
meEl.textContent = '● ' + presence.name;
meEl.style.color = presence.color;

// ── App state ─────────────────────────────────────────────────────────────────
let activeTool = 'select';
let isDrawing  = false;
let drawStart  = null;
let drawShape  = null;
let penPoints  = [];
let justDrew   = false;     // suppress click-to-select right after finishing a draw
let selected   = null;
let comments   = [];
let pins       = {};
let flashes    = [];

// ── Tool management ───────────────────────────────────────────────────────────

function setActiveTool(tool) {
    activeTool = tool;
    document.querySelectorAll('.tool[data-tool]').forEach((b) =>
        b.classList.toggle('active', b.dataset.tool === tool));
    stage.container().style.cursor =
        tool === 'select' ? 'default' :
        tool === 'text'   ? 'text'    : 'crosshair';
}

document.querySelectorAll('.tool[data-tool]').forEach((btn) => {
    btn.addEventListener('click', () => {
        const tool = btn.dataset.tool;
        if (tool === 'image') { handleImageTool(); return; }
        setActiveTool(tool);
        // Activating a draw tool clears current selection
        if (tool !== 'select') {
            tr.nodes([]); selected = null; layer.batchDraw(); updateControls();
        }
    });
});

// ── Keyboard shortcuts ────────────────────────────────────────────────────────
const KEY_MAP = {
    v:'select', r:'rect', e:'ellipse', l:'line', a:'arrow',
    p:'pen', t:'text', s:'star', g:'polygon', i:'image',
};

window.addEventListener('keydown', (ev) => {
    const tag = document.activeElement.tagName;
    if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return;

    const k = ev.key;
    if (k === 'Escape') {
        setActiveTool('select');
        if (isDrawing) { cancelDraw(); }
        else { tr.nodes([]); selected = null; layer.batchDraw(); updateControls(); }
        return;
    }
    if (k === 'Delete' || k === 'Backspace') { deleteSelected(); return; }
    if (k === ']') { bringForward(); return; }
    if (k === '[') { sendBackward(); return; }

    const tool = KEY_MAP[k.toLowerCase()];
    if (tool) {
        if (tool === 'image') { handleImageTool(); return; }
        setActiveTool(tool);
        if (tool !== 'select') { tr.nodes([]); selected = null; layer.batchDraw(); updateControls(); }
    }
});

// ── Helper: read current color/style defaults ─────────────────────────────────
const getFill        = () => document.getElementById('fillColor').value;
const getStroke      = () => document.getElementById('strokeColor').value;
const getStrokeWidth = () => +document.getElementById('strokeWidth').value || 2;
const getOpacity     = () => (+document.getElementById('opacity').value || 100) / 100;
const getPolySides   = () => +document.getElementById('shapeSides').value || 6;
const getStarPts     = () => +document.getElementById('shapeSides').value || 5;
const getStarInner   = () => (+document.getElementById('starInner').value || 45) / 100;

const shapeBaseAttrs = () => ({
    fill: getFill(), stroke: getStroke(), strokeWidth: getStrokeWidth(),
    opacity: getOpacity(), draggable: true, name: 'shape',
});

// ── Drawing event handlers ────────────────────────────────────────────────────

stage.on('mousedown touchstart', (e) => {
    if (activeTool === 'select') return;

    // Clicking an existing shape while a draw tool is active → switch to select
    if (e.target !== stage && e.target.hasName('shape') && activeTool !== 'pen') {
        setActiveTool('select');
        return;
    }

    const pos = stage.getPointerPosition();
    if (!pos) return;
    drawStart  = { ...pos };
    isDrawing  = true;
    const base = shapeBaseAttrs();

    switch (activeTool) {
        case 'rect':
            drawShape = new Konva.Rect({ ...base, x: pos.x, y: pos.y, width: 0, height: 0 });
            break;
        case 'ellipse':
            drawShape = new Konva.Ellipse({ ...base, x: pos.x, y: pos.y, radiusX: 1, radiusY: 1 });
            break;
        case 'line':
            drawShape = new Konva.Line({
                stroke: base.stroke, strokeWidth: base.strokeWidth,
                opacity: base.opacity, name: 'shape', draggable: true,
                lineCap: 'round', points: [pos.x, pos.y, pos.x, pos.y],
            });
            break;
        case 'arrow':
            drawShape = new Konva.Arrow({
                stroke: base.stroke, fill: base.stroke,
                strokeWidth: base.strokeWidth, opacity: base.opacity,
                name: 'shape', draggable: true,
                pointerLength: 12, pointerWidth: 10,
                points: [pos.x, pos.y, pos.x, pos.y],
            });
            break;
        case 'pen':
            penPoints = [pos.x, pos.y, pos.x, pos.y];
            drawShape = new Konva.Line({
                stroke: base.stroke, strokeWidth: base.strokeWidth,
                opacity: base.opacity, name: 'shape', draggable: true,
                lineCap: 'round', lineJoin: 'round', tension: 0.5,
                points: penPoints,
            });
            break;
        case 'star': {
            const pts = getStarPts();
            drawShape = new Konva.Star({
                ...base, x: pos.x, y: pos.y,
                numPoints: pts, outerRadius: 1, innerRadius: 1 * getStarInner(),
            });
            break;
        }
        case 'polygon': {
            const sides = getPolySides();
            drawShape = new Konva.RegularPolygon({ ...base, x: pos.x, y: pos.y, sides, radius: 1 });
            break;
        }
        case 'text': {
            // Click-to-place: create text node immediately and enter edit mode
            const node = new Konva.Text({
                ...base, id: uuid(), x: pos.x, y: pos.y,
                text: 'Double-click to edit',
                fontSize: +document.getElementById('fontSize').value || 22,
                fontFamily: document.getElementById('fontFamily').value || 'system-ui, sans-serif',
                fill: base.fill, padding: 4,
            });
            layer.add(node);
            layer.batchDraw();
            session.emitShape(node);
            selectShape(node);
            setActiveTool('select');
            // immediately open text editor
            setTimeout(() => editText(node), 60);
            isDrawing = false;
            drawShape  = null;
            return;
        }
    }

    if (drawShape) { layer.add(drawShape); layer.batchDraw(); }
});

stage.on('mousemove touchmove', (e) => {
    if (!isDrawing || !drawShape) return;
    const pos = stage.getPointerPosition();
    if (!pos) return;
    const shift = e.evt && e.evt.shiftKey;
    updateDrawShape(pos, shift);
    layer.batchDraw();
});

stage.on('mouseup touchend', () => {
    if (!isDrawing) return;
    isDrawing = false;
    penPoints  = [];

    if (!drawShape) return;

    // Discard shapes that are too small (accidental clicks)
    if (isTooSmall(drawShape)) {
        drawShape.destroy();
        drawShape = null;
        layer.batchDraw();
        setActiveTool('select');
        return;
    }

    drawShape.id(uuid());
    const finalShape = drawShape;
    drawShape = null;

    layer.batchDraw();
    session.emitShape(finalShape);
    selectShape(finalShape);

    justDrew = true;
    setTimeout(() => { justDrew = false; }, 120);

    setActiveTool('select');
});

function updateDrawShape(pos, constrain) {
    let dx = pos.x - drawStart.x;
    let dy = pos.y - drawStart.y;

    switch (activeTool) {
        case 'rect': {
            if (constrain) { const s = Math.max(Math.abs(dx), Math.abs(dy)); dx = Math.sign(dx||1)*s; dy = Math.sign(dy||1)*s; }
            const x = Math.min(pos.x, drawStart.x), y = Math.min(pos.y, drawStart.y);
            drawShape.setAttrs({ x, y, width: Math.abs(dx), height: Math.abs(dy) });
            break;
        }
        case 'ellipse': {
            if (constrain) { const r = Math.max(Math.abs(dx), Math.abs(dy)); dx = Math.sign(dx||1)*r; dy = Math.sign(dy||1)*r; }
            drawShape.setAttrs({
                x: drawStart.x + dx / 2, y: drawStart.y + dy / 2,
                radiusX: Math.abs(dx) / 2, radiusY: Math.abs(dy) / 2,
            });
            break;
        }
        case 'line':
        case 'arrow': {
            let ex = pos.x, ey = pos.y;
            if (constrain) {
                const angle = Math.atan2(dy, dx);
                const snap  = Math.round(angle / (Math.PI / 4)) * (Math.PI / 4);
                const len   = Math.sqrt(dx * dx + dy * dy);
                ex = drawStart.x + Math.cos(snap) * len;
                ey = drawStart.y + Math.sin(snap) * len;
            }
            drawShape.points([drawStart.x, drawStart.y, ex, ey]);
            break;
        }
        case 'pen': {
            penPoints = penPoints.concat([pos.x, pos.y]);
            drawShape.points(penPoints);
            break;
        }
        case 'star': {
            const r = Math.sqrt(dx * dx + dy * dy);
            drawShape.outerRadius(r);
            drawShape.innerRadius(r * getStarInner());
            break;
        }
        case 'polygon': {
            const r = Math.sqrt(dx * dx + dy * dy);
            drawShape.radius(r);
            break;
        }
    }
}

function isTooSmall(shape) {
    const cls = shape.getClassName();
    if (cls === 'Line' || cls === 'Arrow') {
        const pts = shape.points();
        if (pts.length < 4) return true;
        const d = Math.hypot(pts[pts.length-2]-pts[0], pts[pts.length-1]-pts[1]);
        return d < 5;
    }
    if (cls === 'Ellipse') return shape.radiusX() < 3;
    if (cls === 'Star')    return shape.outerRadius() < 5;
    if (cls === 'RegularPolygon') return shape.radius() < 5;
    return (shape.width?.() ?? 0) < 5 || (shape.height?.() ?? 0) < 5;
}

function cancelDraw() {
    isDrawing = false;
    penPoints  = [];
    if (drawShape) { drawShape.destroy(); drawShape = null; layer.batchDraw(); }
}

// ── Selection (event delegation — covers local AND remote shapes) ─────────────

stage.on('click tap', (e) => {
    if (justDrew) return;
    if (activeTool !== 'select') {
        // Clicking on a shape while a draw tool is active → switch to select
        if (e.target !== stage && e.target.hasName('shape')) {
            setActiveTool('select');
            selectShape(e.target);
        }
        return;
    }
    if (e.target === stage) {
        tr.nodes([]); selected = null; layer.batchDraw(); updateControls(); return;
    }
    if (!e.target.hasName('shape')) return; // transformer handles, etc.
    selectShape(e.target);
});

stage.on('dblclick dbltap', (e) => {
    const node = e.target;
    if (node.hasName('shape') && node.getClassName() === 'Text') editText(node);
});

stage.on('dragend', (e) => {
    if (e.target && e.target.hasName('shape')) session.emitShape(e.target);
});

tr.on('transformend', () => {
    tr.nodes().forEach((n) => session.emitShape(n));
});

function selectShape(node) {
    tr.nodes([node]);
    selected = node;
    layer.batchDraw();
    updateControls();
}

// ── Delete ────────────────────────────────────────────────────────────────────

function deleteSelected() {
    if (!selected) return;
    const id = selected.id();
    selected.destroy();
    tr.nodes([]); selected = null;
    layer.batchDraw();
    updateControls();
    session.emitDelete(id);
}
document.getElementById('del').onclick = deleteSelected;

// ── Z-order ───────────────────────────────────────────────────────────────────

function bringForward() {
    if (!selected) return;
    selected.moveToTop(); tr.moveToTop(); layer.batchDraw();
}
function sendBackward() {
    if (!selected) return;
    selected.moveToBottom(); tr.moveToTop(); layer.batchDraw();
}
document.getElementById('bringFwd').onclick = bringForward;
document.getElementById('sendBwd').onclick  = sendBackward;

// ── Property panel: live controls ────────────────────────────────────────────

function onShapeProp(fn) {
    return (e) => {
        if (!selected) return;
        fn(e.target.value, e.target);
        layer.batchDraw();
        session.emitShape(selected);
        // update output labels inline
        updateOutputs();
    };
}

document.getElementById('fillColor').oninput = onShapeProp((v) => selected.fill(v));
document.getElementById('strokeColor').oninput = onShapeProp((v) => selected.stroke(v));
document.getElementById('strokeWidth').oninput = onShapeProp((v) => selected.strokeWidth(+v));
document.getElementById('opacity').oninput = onShapeProp((v) => selected.opacity(+v / 100));
document.getElementById('cornerRadius').oninput = onShapeProp((v) => selected.cornerRadius?.(+v));
document.getElementById('lineHeight').oninput = onShapeProp((v) => selected.lineHeight?.(+v / 100));
document.getElementById('fontSize').oninput = onShapeProp((v) => selected.fontSize?.(+v));
document.getElementById('fontFamily').onchange = onShapeProp((v) => selected.fontFamily?.(v));

document.getElementById('noFill').onclick = () => {
    if (!selected) return;
    selected.fill('transparent');
    document.getElementById('fillColor').value = '#000000';
    layer.batchDraw();
    session.emitShape(selected);
};

// shapeSides: star numPoints or polygon sides
document.getElementById('shapeSides').oninput = onShapeProp((v) => {
    const cls = selected.getClassName();
    if (cls === 'Star') {
        selected.numPoints(+v);
        selected.innerRadius(selected.outerRadius() * getStarInner());
    } else if (cls === 'RegularPolygon') {
        selected.sides(+v);
    }
});

// starInner: inner radius ratio for stars
document.getElementById('starInner').oninput = onShapeProp((v) => {
    if (selected.getClassName() !== 'Star') return;
    selected.innerRadius(selected.outerRadius() * (+v / 100));
});

// Text style toggles
function toggleFontStyle(style) {
    if (!selected || selected.getClassName() !== 'Text') return;
    let fs = (selected.fontStyle() || '').replace(style, '').trim();
    if (!((selected.fontStyle() || '').includes(style))) fs = (fs + ' ' + style).trim();
    selected.fontStyle(fs);
    layer.batchDraw();
    session.emitShape(selected);
    updateControls();
}
function toggleTextDeco(style) {
    if (!selected || selected.getClassName() !== 'Text') return;
    const cur = selected.textDecoration() || '';
    const next = cur.includes(style)
        ? cur.replace(style, '').trim()
        : (cur + ' ' + style).trim();
    selected.textDecoration(next);
    layer.batchDraw();
    session.emitShape(selected);
    updateControls();
}
function setAlign(align) {
    if (!selected || selected.getClassName() !== 'Text') return;
    selected.align(align);
    layer.batchDraw();
    session.emitShape(selected);
    updateControls();
}

document.getElementById('fmtBold').onclick      = () => toggleFontStyle('bold');
document.getElementById('fmtItalic').onclick    = () => toggleFontStyle('italic');
document.getElementById('fmtUnderline').onclick = () => toggleTextDeco('underline');
document.getElementById('fmtStrike').onclick    = () => toggleTextDeco('line-through');
document.getElementById('alignLeft').onclick    = () => setAlign('left');
document.getElementById('alignCenter').onclick  = () => setAlign('center');
document.getElementById('alignRight').onclick   = () => setAlign('right');

// ── Update controls from selected shape ──────────────────────────────────────

function updateControls() {
    const propsEl      = document.getElementById('shape-props');
    const fillRow      = document.querySelector('.fill-row');
    const textPropsEl  = document.getElementById('text-props');
    const rectPropsEl  = document.getElementById('rect-props');
    const spPropsEl    = document.getElementById('star-poly-props');
    const starInnerRow = document.getElementById('star-inner-row');
    const cmAdd        = document.getElementById('cmAdd');

    propsEl.style.display = selected ? 'block' : 'none';

    if (selected) {
        const cls = selected.getClassName();
        const isLineType = cls === 'Line' || cls === 'Arrow';
        const isText     = cls === 'Text';
        const isRect     = cls === 'Rect';
        const isStar     = cls === 'Star';
        const isPoly     = cls === 'RegularPolygon';

        // Fill row: hidden for line/arrow (stroke-only)
        fillRow.style.display = isLineType ? 'none' : 'flex';
        if (!isLineType) {
            document.getElementById('fillColor').value = toHex(selected.fill());
        }

        // Stroke
        document.getElementById('strokeColor').value = toHex(selected.stroke() || '#000000');
        document.getElementById('strokeWidth').value = selected.strokeWidth?.() ?? 2;

        // Opacity
        document.getElementById('opacity').value = Math.round((selected.opacity?.() ?? 1) * 100);

        // Rect corner radius
        rectPropsEl.style.display = isRect ? 'block' : 'none';
        if (isRect) {
            const cr  = selected.cornerRadius?.() ?? 0;
            const crv = Array.isArray(cr) ? cr[0] : cr;
            document.getElementById('cornerRadius').value = crv;
        }

        // Star / Polygon
        spPropsEl.style.display = (isStar || isPoly) ? 'block' : 'none';
        starInnerRow.style.display = isStar ? 'flex' : 'none';
        if (isStar || isPoly) {
            document.getElementById('sidesLabel').textContent = isStar ? 'Points' : 'Sides';
            const pts = isStar ? (selected.numPoints?.() ?? 5) : (selected.sides?.() ?? 6);
            document.getElementById('shapeSides').value = pts;
        }
        if (isStar) {
            const ratio = Math.round((selected.innerRadius?.() / (selected.outerRadius?.() || 1)) * 100);
            document.getElementById('starInner').value = ratio;
        }

        // Text
        textPropsEl.style.display = isText ? 'block' : 'none';
        if (isText) {
            document.getElementById('fontFamily').value = selected.fontFamily?.() || 'system-ui, sans-serif';
            document.getElementById('fontSize').value = selected.fontSize?.() ?? 22;
            document.getElementById('lineHeight').value = Math.round((selected.lineHeight?.() ?? 1) * 100);
            const fs = selected.fontStyle?.() || '';
            const td = selected.textDecoration?.() || '';
            const al = selected.align?.() || 'left';
            document.getElementById('fmtBold').classList.toggle('active',      fs.includes('bold'));
            document.getElementById('fmtItalic').classList.toggle('active',    fs.includes('italic'));
            document.getElementById('fmtUnderline').classList.toggle('active', td.includes('underline'));
            document.getElementById('fmtStrike').classList.toggle('active',    td.includes('line-through'));
            document.getElementById('alignLeft').classList.toggle('active',    al === 'left');
            document.getElementById('alignCenter').classList.toggle('active',  al === 'center');
            document.getElementById('alignRight').classList.toggle('active',   al === 'right');
        }

        updateOutputs();
    }

    cmAdd.disabled = !selected;
    cmAdd.textContent = selected ? 'Comment on selected' : 'Select a shape first';
    renderPanel();
}

function updateOutputs() {
    const o = (id, val) => { const el = document.getElementById(id); if (el) el.value = val; };
    o('strokeWidthVal', document.getElementById('strokeWidth').value);
    o('opacityVal',     document.getElementById('opacity').value + '%');
    o('cornerRadiusVal', document.getElementById('cornerRadius').value);
    o('shapeSidesVal',  document.getElementById('shapeSides').value);
    o('starInnerVal',   document.getElementById('starInner').value + '%');
    o('lineHeightVal',  (+document.getElementById('lineHeight').value / 100).toFixed(1));
}

function toHex(c) {
    if (!c || c === 'transparent' || c === '') return '#000000';
    if (c.startsWith('#')) return c.slice(0, 7);
    try {
        const m = c.match(/\d+/g);
        if (!m || m.length < 3) return '#000000';
        return '#' + m.slice(0, 3).map((n) => (+n).toString(16).padStart(2, '0')).join('');
    } catch { return '#000000'; }
}

// ── Text editing overlay ──────────────────────────────────────────────────────

function editText(textNode) {
    textNode.hide(); tr.hide(); layer.batchDraw();

    const stageBox = stage.container().getBoundingClientRect();
    const abs      = textNode.absolutePosition();
    const scaleX   = textNode.scaleX() || 1;
    const fs       = (textNode.fontSize() || 22) * scaleX;

    const ta = document.createElement('textarea');
    document.body.appendChild(ta);
    ta.value = textNode.text();
    Object.assign(ta.style, {
        position:        'fixed',
        left:            (stageBox.left + abs.x) + 'px',
        top:             (stageBox.top  + abs.y) + 'px',
        minWidth:        Math.max(80, (textNode.width() || 0) * scaleX) + 'px',
        minHeight:       fs * 1.5 + 'px',
        fontSize:        fs + 'px',
        fontFamily:      textNode.fontFamily() || 'system-ui',
        fontStyle:       (textNode.fontStyle() || '').includes('italic') ? 'italic' : 'normal',
        fontWeight:      (textNode.fontStyle() || '').includes('bold') ? 'bold' : 'normal',
        textDecoration:  textNode.textDecoration() || 'none',
        textAlign:       textNode.align() || 'left',
        lineHeight:      (textNode.lineHeight() || 1).toString(),
        color:           textNode.fill() || '#000',
        background:      'rgba(255,255,255,.95)',
        border:          '2px dashed #2d6cdf',
        outline:         'none',
        resize:          'none',
        overflow:        'hidden',
        padding:         '3px 6px',
        borderRadius:    '3px',
        transform:       `rotate(${textNode.rotation()}deg)`,
        transformOrigin: '0 0',
        zIndex:          '999',
    });
    ta.rows = 2;
    ta.focus(); ta.select();

    function commit() {
        textNode.text(ta.value.trim() || 'Text');
        textNode.show(); tr.show(); tr.forceUpdate();
        layer.batchDraw(); ta.remove();
        session.emitShape(textNode);
    }
    ta.addEventListener('blur', commit);
    ta.addEventListener('keydown', (ev) => {
        if (ev.key === 'Enter' && !ev.shiftKey) { ev.preventDefault(); ta.blur(); }
        if (ev.key === 'Escape') {
            ta.removeEventListener('blur', commit);
            ta.remove(); textNode.show(); tr.show(); layer.batchDraw();
        }
    });
}

// ── Image tool ────────────────────────────────────────────────────────────────

function handleImageTool() {
    const url = window.prompt('Image URL (must allow cross-origin):',
        'https://picsum.photos/300/200');
    if (!url) return;
    setActiveTool('select');

    const domImg = new Image();
    domImg.crossOrigin = 'anonymous';
    domImg.onload = () => {
        const maxSide = 300;
        let w = domImg.width, h = domImg.height;
        const scale = Math.min(1, maxSide / w, maxSide / h);
        w = Math.round(w * scale); h = Math.round(h * scale);

        const kImg = new Konva.Image({
            id: uuid(), image: domImg, x: 80 + Math.random() * 60, y: 80 + Math.random() * 60,
            width: w, height: h, draggable: true, name: 'shape',
        });
        kImg._srcUrl = url;
        layer.add(kImg);
        layer.batchDraw();
        session.emitShape(kImg);
        selectShape(kImg);
    };
    domImg.onerror = () => alert('Could not load image — the server may not allow cross-origin requests.');
    domImg.src = url;
}

// ── ColabSession setup ────────────────────────────────────────────────────────

const session = new CollabSession({
    url: '/collab', documentId: 'konva-canvas', layer, presence,
    onStatus: (s) => { document.getElementById('status').textContent = s; },
    onPeers:        renderPeers,
    onRemoteEdit:   flashEdit,
    onRemoteDelete: (id) => {
        if (selected && selected.id() === id) {
            tr.nodes([]); selected = null; layer.batchDraw(); updateControls();
        }
    },
    onComments: (list) => { comments = list; renderPins(); renderPanel(); },
});

document.getElementById('cmAdd').onclick = async () => {
    const ta = document.getElementById('cmBody');
    const body = ta.value.trim();
    if (!body || !selected) return;
    ta.value = '';
    try { await session.addComment(selected.id(), body); }
    catch (e) { console.error('addComment', e); }
};

session.connect().then(() => updateControls()).catch((e) => console.error('[collab]', e));
window.__collab = session;

// ── Overlay rAF loop (pins + flash labels) ────────────────────────────────────

const overlay = document.getElementById('overlay');

function shapeBBox(id) {
    const node = layer.findOne('#' + id);
    if (!node) return null;
    const box = stage.container().getBoundingClientRect();
    try {
        const r = node.getClientRect({ relativeTo: layer });
        return { x: box.left + r.x, y: box.top + r.y,
                 right: box.left + r.x + r.width, bottom: box.top + r.y + r.height };
    } catch {
        const abs = node.absolutePosition();
        return { x: box.left + abs.x, y: box.top + abs.y,
                 right: box.left + abs.x + 40, bottom: box.top + abs.y + 40 };
    }
}

function tick() {
    for (const id of Object.keys(pins)) {
        const el = pins[id];
        const b  = shapeBBox(id);
        if (!b) { el.style.display = 'none'; continue; }
        el.style.display = '';
        el.style.left = (b.right + 4) + 'px';
        el.style.top  = b.y + 'px';
    }
    const now = Date.now();
    flashes = flashes.filter((f) => {
        if (now > f.until) { f.el.remove(); return false; }
        const b = shapeBBox(f.id);
        if (!b) { f.el.style.display = 'none'; return true; }
        f.el.style.display = '';
        f.el.style.left = b.x + 'px';
        f.el.style.top  = (b.y - 24) + 'px';
        return true;
    });
    requestAnimationFrame(tick);
}
requestAnimationFrame(tick);

// ── Remote-edit feedback ──────────────────────────────────────────────────────

function flashEdit(shapeId, peer) {
    const node = layer.findOne('#' + shapeId);
    if (node && typeof node.stroke === 'function') {
        const prevS  = node.stroke();
        const prevSW = node.strokeWidth?.() ?? 0;
        node.stroke(peer.color); node.strokeWidth(Math.max(prevSW, 3));
        layer.batchDraw();
        setTimeout(() => { node.stroke(prevS); node.strokeWidth(prevSW); layer.batchDraw(); }, 2500);
    }
    const el = document.createElement('div');
    el.className = 'flash'; el.style.background = peer.color; el.textContent = peer.name;
    overlay.appendChild(el);
    flashes.push({ id: shapeId, el, until: Date.now() + 2600 });
}

// ── Peers ─────────────────────────────────────────────────────────────────────

function renderPeers(peers) {
    const el     = document.getElementById('peers');
    const others = peers.filter((p) => p.name !== presence.name);
    const dot    = (c) => `<span class="dot" style="background:${c}"></span>`;
    el.innerHTML =
        `<div class="peer">${dot(presence.color)}${presence.name} <span class="you">(you)</span></div>` +
        others.map((p) => `<div class="peer">${dot(p.color)}${p.name}</div>`).join('');
    renderPanel();
}

// ── Comment pins ──────────────────────────────────────────────────────────────

function renderPins() {
    overlay.querySelectorAll('.pin').forEach((e) => e.remove());
    pins = {};
    const byId = {};
    for (const c of comments) {
        if (!c.shapeId) continue;
        (byId[c.shapeId] ??= []).push(c);
    }
    for (const [id, list] of Object.entries(byId)) {
        const pin = document.createElement('div');
        pin.className = 'pin';
        pin.textContent = '💬 ' + list.length;
        pin.onclick = () => { const n = layer.findOne('#' + id); if (n) selectShape(n); };
        overlay.appendChild(pin);
        pins[id] = pin;
    }
}

// ── Comment panel ─────────────────────────────────────────────────────────────

function renderPanel() {
    const panel    = document.getElementById('panel');
    const head     = document.getElementById('cmHead');
    const selectedId = selected ? selected.id() : null;
    const shown    = selectedId ? comments.filter((c) => c.shapeId === selectedId) : comments;

    head.textContent = selectedId
        ? 'Comments · selected shape'
        : `Comments · all (${comments.length})`;

    if (!shown.length) {
        panel.innerHTML = `<p class="empty">No open comments${selectedId ? ' on this shape' : ''}.</p>`;
        return;
    }
    panel.innerHTML = '';
    for (const c of shown) {
        const author = session.peerByConn(c.authorPeerId);
        const name   = author ? author.name  : 'Someone';
        const color  = author ? author.color : '#9aa8b8';

        const row = document.createElement('div');
        row.className = 'cm';
        row.innerHTML =
            `<div class="body"></div>` +
            `<div class="meta"><span class="dot" style="background:${color}"></span>` +
            `<span class="who"></span>` +
            `${!selectedId ? ' · <span class="lnk">go to shape</span>' : ''}</div>`;
        row.querySelector('.body').textContent = c.body;
        row.querySelector('.who').textContent  = name;

        const res = document.createElement('button');
        res.className = 'resolve'; res.title = 'Resolve'; res.textContent = '✓';
        res.onclick = () => session.resolveComment(c.id).catch((e) => console.error(e));
        row.appendChild(res);

        const lnk = row.querySelector('.lnk');
        if (lnk) {
            lnk.onclick = () => { const n = layer.findOne('#' + c.shapeId); if (n) selectShape(n); };
        }
        panel.appendChild(row);
    }
}
