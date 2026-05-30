import { OpStreamSession } from 'opstream-collab';

// Kanban adapter for OpStream. All transport/outbox/snapshot plumbing lives in
// `opstream-collab`; here we just map cards ⇄ ops.
//
// Each card is a register at `cards.<id>` = { text, column, order }. Adding,
// editing, moving (drag between columns) or deleting a card is a JSON set/del;
// remote ops update the local card map and trigger a re-render.

const PREFIX = 'cards.';

export class CollabSession {
    constructor({ url, documentId, onStatus, onRender }) {
        this.onRender = onRender || (() => {});
        this.cards = new Map(); // id -> { text, column, order }

        this.session = new OpStreamSession({
            url, documentId,
            onStatus,
            applyOps: (ops) => {
                for (const op of ops) {
                    if (!op.path.startsWith(PREFIX)) continue;
                    const id = op.path.slice(PREFIX.length);
                    if (op.isDelete) this.cards.delete(id);
                    else this.cards.set(id, op.value);
                }
                this.onRender(this.cards);
            },
        });
    }

    async connect() {
        await this.session.connect();
        this.onRender(this.cards); // ensure an initial paint even if the snapshot was empty
    }

    // ── Local mutations (called from the UI) ──────────────────────────────────
    upsertCard(id, card) {
        this.cards.set(id, card);              // optimistic local update
        this.session.setPath(PREFIX + id, card);
    }

    deleteCard(id) {
        this.cards.delete(id);
        this.session.delPath(PREFIX + id);
    }
}
