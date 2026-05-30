# Docs revamp plan — "outcome first, guts last"

Goal of this revamp: a newcomer who has **never heard of OpStream** should, within
30 seconds on the landing page, understand **what it is**, **what they can build
with it**, and **how to try it**. All the server internals (routers, sessions,
engine contracts, wire protocol) move to the **bottom** — most readers never
touch them.

---

## 1. What's wrong today

| Problem | Where | Effect |
|---|---|---|
| Leads with feature-count bragging ("Eight engines…") | `index.md` §"What OpStream gives you" | Reader still doesn't know *what it is* |
| Architecture diagram is the **2nd** thing on the page | `index.md` §"Mental model" | Internals before value |
| Jargon undefined up front: *engine, op, backplane, IDocumentStore, builder API* | `index.md`, nav | Scares non-experts |
| "Concepts" (DocumentRouter, Session, Peer, Revision, Compaction…) sits **2nd in the nav** | `mkdocs.yml` | Pure internals near the top |
| No "what can I build" showcase early | whole site | No hook |
| Recipes (the actual outcomes) are buried at nav position 4 | `mkdocs.yml` | The best selling point is hidden |

The content itself is good — the **order** and the **first impression** are wrong.

---

## 2. The new story arc (top → bottom)

Every doc site for a tool like this should flow audience-first:

1. **What is it?** — plain language + an analogy everyone knows (Google Docs).
2. **What can I build?** — concrete, visual outcomes, not features.
3. **Why this and not X?** — short honest fit/no-fit.
4. **Try it in 5 minutes** — one CTA.
5. *(scroll / deeper)* **How it works** — the architecture, for the curious.
6. *(bottom)* **The guts** — concepts, internals, engine contracts, wire protocol.

> Rule of thumb: nothing with a generic type parameter (`IOpEngine<TDoc,TOp>`)
> or an interface name should appear **above the fold** on the landing page.

---

## 3. Proposed new `index.md` (full draft)

This is ready to drop in. Screenshots/GIFs are placeholders — swap in real ones
from the samples (Monaco, Radzen, the browser extension demo, etc.).

```markdown
# OpStream

**Let several people edit the same thing at the same time — inside your own .NET app.**

You know how Google Docs lets a whole team type into one document and everyone
sees every keystroke instantly, with live cursors and no "who has the file open?"
emails? OpStream brings that to **your** application — your editor, your data,
your login — instead of sending your users off to someone else's cloud.

You keep the app you already have. OpStream adds the real-time, multi-user layer.

[Try it in 5 minutes :material-arrow-right:](getting-started/quickstart.md){ .md-button .md-button--primary }
[See what you can build :material-arrow-right:](#what-can-you-build){ .md-button }

---

## What can you build?

<div class="grid cards" markdown>

-   :material-text-box-edit: **A shared text or code editor**

    Two people type into the same document; edits merge without clobbering
    each other. Live cursors and selections included.

    [:octicons-arrow-right-24: Recipe](recipes/collaborative-text-editor.md)

-   :material-file-tree: **A Notion-style block document**

    Nested, reorderable blocks that several editors can move and rewrite at
    once.

    [:octicons-arrow-right-24: Recipe](recipes/notion-blocks.md)

-   :material-table: **A multi-user spreadsheet**

    Many people editing cells live, with conflicts resolved per-cell.

    [:octicons-arrow-right-24: Recipe](recipes/spreadsheet.md)

-   :material-form-select: **A settings dialog edited by a team**

    A plain form where two admins changing different fields don't overwrite
    each other.

    [:octicons-arrow-right-24: Recipe](recipes/settings-form.md)

</div>

And the things that make it feel "live":

- **Live cursors & presence** — see where everyone else is, and who's typing.
- **Per-user undo / redo** — your Ctrl-Z undoes *your* last change, not your
  teammate's.
- **Offline & reconnect** — clients catch up from where they left off.

> ![A collaborative editor with two cursors](assets/demo-collab.gif)
> *(placeholder — drop a real GIF of the Monaco or Radzen sample here)*

---

## Is OpStream the right tool?

| You want… | Fit |
|---|---|
| Several users editing one document at once | :material-check: yes |
| Live cursors, selections, "is typing" presence | :material-check: yes |
| To keep your existing ASP.NET Core login and database | :material-check: yes |
| To grow from one server to many without a rewrite | :material-check: yes |
| Just a fancy text editor for **one** user | :material-close: use Quill / TipTap |
| A general message bus | :material-close: use NATS / Kafka |

---

## Try it now

The fastest taste — one command, no setup:

```bash
docker run -p 8080:8080 opstreamcollab/opstream
```

Then open the quickstart to wire it to a real editor:

[5-minute quickstart :material-rocket-launch:](getting-started/quickstart.md){ .md-button .md-button--primary }

---

## How it works (the short version)

You only need this if you're curious — you can build a lot without it.

OpStream sits between your editor and your database. When someone makes an edit,
it travels as a tiny **operation** ("insert 'x' at position 5"), gets merged with
everyone else's operations so the result is consistent for all, and is saved so
late-joiners and reconnecting clients can catch up.

You choose three things, and OpStream handles the rest:

- **What kind of document** you're editing (text, rich text, a tree, a table, a
  form…) — these are the *engines*.
- **How clients connect** (SignalR, WebSockets, or gRPC) — the *transports*.
- **Where data is stored** (SQL Server, PostgreSQL, Redis, in-memory, and more).

[The full architecture :material-arrow-right:](architecture.md)

---

## Going deeper

- **[Choosing your pieces](engines/index.md)** — engines, transports, storage.
- **[Running in production](operations/backplane.md)** — scaling, auth, deploy.
- **[Under the hood](concepts/document.md)** — the internal model, lifecycle, and
  wire protocol, for contributors and the deeply curious.
```

Key moves in this draft:
- Opens with a **plain-language sentence + the Google Docs analogy**, no nouns
  the reader has to look up.
- "What can you build" is **second**, built from the **real recipes** that
  already exist, framed as outcomes ("a shared editor") not features ("an engine").
- The architecture is demoted to "the short version" near the bottom, explicitly
  labelled *only if you're curious*.
- The internal **Concepts** pages are reached only via "Under the hood" at the
  very end.

---

## 4. Proposed new `nav` (in `mkdocs.yml`)

Reordered audience-first. Internals sink to the bottom; outcomes rise.

```yaml
nav:
  - Home: index.md

  - Get started:
      - Run with Docker: getting-started/docker.md
      - Install the packages: getting-started/installation.md
      - 5-minute quickstart: getting-started/quickstart.md

  - What can you build:           # promoted — was "Recipes" at position 4
      - Collaborative text editor: recipes/collaborative-text-editor.md
      - Notion-style block document: recipes/notion-blocks.md
      - Multi-user spreadsheet: recipes/spreadsheet.md
      - Shared settings form: recipes/settings-form.md

  - Choose your pieces:
      - Document types (engines):
          - Overview: engines/index.md
          - Text OT: engines/text-ot.md
          - Rich Text: engines/rich-text.md
          - JSON CRDT: engines/json-crdt.md
          - Tree CRDT: engines/tree-crdt.md
          - Table CRDT: engines/table-crdt.md
          - Form OT: engines/form-ot.md
          - Awareness (presence): engines/awareness.md
          - Undo / Redo: engines/undo-redo.md
      - Connections (transports):
          - Overview: transports/index.md
          - SignalR: transports/signalr.md
          - WebSockets: transports/websockets.md
          - gRPC: transports/grpc.md
      - Storage:
          - Overview: storage/index.md
          - In-memory: storage/memory.md
          - Entity Framework Core: storage/ef-core.md
          - SQL Server: storage/sql-server.md
          - PostgreSQL: storage/postgresql.md
          - MySQL: storage/mysql.md
          - SQLite: storage/sqlite.md
          - MongoDB: storage/mongodb.md
          - Redis: storage/redis.md

  - Run in production:
      - Scaling out (backplane): operations/backplane.md
      - Authorization: operations/authorization.md
      - Multi-tenancy: operations/multitenancy.md
      - Observability: operations/observability.md
      - Snapshots: operations/snapshots.md
      - Deployment: operations/deployment.md
      - Docker image: operations/docker.md

  # ---- everything below is "the guts": for contributors & the deeply curious ----

  - How it works:
      - Architecture: architecture.md
      - Core concepts: getting-started/concepts.md
      - Document: concepts/document.md
      - Document Router: concepts/document-router.md
      - Session: concepts/session.md
      - Peer: concepts/peer.md
      - Revision: concepts/revision.md
      - Comment: concepts/comments.md
      - Comment Router: concepts/comment-router.md
      - Branching: concepts/branching.md
      - Versioning: concepts/versioning.md
      - Merging: concepts/merging.md
      - History: concepts/history.md
      - Compaction: concepts/compaction.md

  - Internals:
      - First user join flow: internals/first-join-flow.md
      - Document lifecycle: internals/document-lifecycle.md

  - Reference:
      - Builder API: reference/builder-api.md
      - Configuration (DI): reference/configuration.md
      - Engine contracts: reference/interfaces.md
      - Wire protocol: reference/wire-protocol.md
```

What changed vs. today:
- **Recipes → "What can you build"**, moved up to position 3 (was 4, below
  Concepts). This is the showcase.
- **Engines + Transports + Storage** grouped under **"Choose your pieces"** with
  plain-language labels ("Document types", "Connections"). Engines was at
  position 7; now it's part of the early "pick what fits" step.
- **Operations → "Run in production"**, with friendlier labels.
- **Concepts (12 internal pages) demoted** from position 2 into **"How it works"**,
  below production. These are reference material, not onboarding.
- **Internals & Reference stay at the very bottom** (already were — good).
- Consider turning off `navigation.expand` in `theme.features` so the long
  lower sections start collapsed and the top of the nav looks short and
  inviting.

---

## 5. Page-level de-jargoning (smaller edits)

Once the landing + nav are done, a light pass on the section overview pages:

- `engines/index.md` — open each engine description with *what you'd build with
  it* before the algorithm name (e.g. "Text OT — for plain-text and code editors"
  before "operational transformation").
- `transports/index.md` — a one-line "which should I pick?" decision hint at the
  top (SignalR if you're already on ASP.NET Core + Blazor/JS; WebSockets for a
  lightweight raw client; gRPC for service-to-service).
- `storage/index.md` — lead with "use in-memory to try it, a real DB for
  production" before the `IDocumentStore` contract.
- Quickstart — make sure the **first** code block produces something the reader
  can *see* (two browser tabs syncing), not just a server booting.

---

## 6. Suggested order of execution

1. Replace `index.md` with the draft in §3 (biggest single win).
2. Reorder `nav` in `mkdocs.yml` per §4; flip `navigation.expand` off.
3. Add a real demo GIF to `assets/` and reference it on the landing.
4. De-jargon the four section overview pages (§5).
5. Re-read the quickstart end-to-end as a first-timer.

Nothing here deletes content — it **reorders** and **reframes**. The deep
technical pages stay exactly where an expert would look for them: at the bottom.
```
