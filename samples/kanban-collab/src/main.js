import { CollabSession } from './collab-session.js';

// Sortable is a global from the CDN <script> in index.html.
const Sortable = window.Sortable;

const COLUMNS = [
    { id: 'todo', title: 'To do' },
    { id: 'doing', title: 'Doing' },
    { id: 'done', title: 'Done' },
];

const board = document.getElementById('board');
const uuid = () => 'c-' + Math.random().toString(36).slice(2, 10);

const session = new CollabSession({
    url: '/collab',
    documentId: 'kanban-demo',
    onStatus: (s) => { const el = document.getElementById('status'); if (el) el.textContent = s; },
    onRender: render,
});

// ── Render the whole board from the card map (cheap for a demo) ───────────────
const listEls = {};
function buildSkeleton() {
    board.innerHTML = '';
    for (const col of COLUMNS) {
        const colEl = document.createElement('div');
        colEl.className = 'column';
        colEl.innerHTML =
            `<header>${col.title} <button class="add" data-col="${col.id}">+</button></header>`;
        const list = document.createElement('div');
        list.className = 'list';
        list.dataset.column = col.id;
        colEl.appendChild(list);
        board.appendChild(colEl);
        listEls[col.id] = list;

        Sortable.create(list, {
            group: 'kanban',
            animation: 150,
            onEnd: onDrop,
        });
    }
    board.querySelectorAll('button.add').forEach((b) => {
        b.onclick = () => addCard(b.dataset.col);
    });
}

function render(cards) {
    for (const col of COLUMNS) listEls[col.id].innerHTML = '';
    const sorted = [...cards.entries()].sort((a, b) => (a[1].order ?? 0) - (b[1].order ?? 0));
    for (const [id, card] of sorted) {
        const list = listEls[card.column] || listEls.todo;
        list.appendChild(cardEl(id, card));
    }
}

function cardEl(id, card) {
    const el = document.createElement('div');
    el.className = 'card';
    el.dataset.id = id;

    const text = document.createElement('div');
    text.className = 'text';
    text.contentEditable = 'true';
    text.textContent = card.text || '';
    text.addEventListener('blur', () => {
        const current = session.cards.get(id);
        if (current && text.textContent !== current.text) {
            session.upsertCard(id, { ...current, text: text.textContent });
        }
    });

    const del = document.createElement('button');
    del.className = 'del';
    del.textContent = '×';
    del.onclick = () => { session.deleteCard(id); render(session.cards); };

    el.appendChild(text);
    el.appendChild(del);
    return el;
}

function addCard(column) {
    const id = uuid();
    const order = Date.now();
    session.upsertCard(id, { text: 'New task', column, order });
    render(session.cards);
}

// Drag finished: update the moved card's column + order from its new DOM position.
function onDrop(evt) {
    const id = evt.item.dataset.id;
    const card = session.cards.get(id);
    if (!card) return;
    const column = evt.to.dataset.column;

    // Order = midpoint between the neighbours it landed between (keeps others stable).
    const siblings = [...evt.to.querySelectorAll('.card')];
    const idx = siblings.indexOf(evt.item);
    const prev = siblings[idx - 1] && session.cards.get(siblings[idx - 1].dataset.id);
    const next = siblings[idx + 1] && session.cards.get(siblings[idx + 1].dataset.id);
    const prevO = prev ? prev.order : (next ? next.order - 2 : Date.now());
    const nextO = next ? next.order : prevO + 2;
    const order = (prevO + nextO) / 2;

    session.upsertCard(id, { ...card, column, order });
}

buildSkeleton();
session.connect().catch((err) => console.error('[collab]', err));
window.__collab = session;
