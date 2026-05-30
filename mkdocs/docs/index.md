# OpStream <span style="float:right"><span style="color: #E34F26">:material-language-html5:</span> <span style="color: #F7DF1E">:material-language-javascript:</span> <span style="color: #512BD4">:material-dot-net:</span> <span style="color: #61DAFB">:material-react:</span> <span style="color: #DD0031">:material-angular:</span></span>

**Let several people edit the same thing at the same time — inside your own app.**

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

!!! tip "Showcase: a collaborative 3D editor"
    The full **[three.js editor](https://threejs.org/editor/)** — *unmodified* —
    turned multiplayer from the outside in a few hundred lines of JavaScript. Add
    and move objects in one browser; a teammate sees them instantly. Proof you can
    make an editor you **don't own** collaborative.
    [See the sample :material-arrow-right:](https://github.com/OpStreamCollab/OpStream/tree/main/samples/threejs-editor)

!!! tip "Showcase: a collaborative spreadsheet"
    The [Luckysheet](https://github.com/dream-num/Luckysheet) spreadsheet —
    *unmodified* — made multiplayer: type in a cell in one browser, a teammate
    sees it instantly. ~150 lines of JavaScript over the JSON engine.
    [See the sample :material-arrow-right:](https://github.com/OpStreamCollab/OpStream/tree/main/samples/luckysheet-collab)

And the things that make it feel "live":

- **Live cursors & presence** — see where everyone else is, and who's typing.
- **Per-user undo / redo** — your ++ctrl+z++ undoes *your* last change, not your
  teammate's.
- **Offline & reconnect** — clients catch up from where they left off.

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

The fastest taste — one command, no .NET SDK, no project:

```bash
docker run -p 8080:8080 opstreamcollab/opstream
```

The server is now listening on `ws://localhost:8080/collab-ws`. Connect from a
few lines of **browser JavaScript** — no .NET on the client:

```html
<textarea id="editor"></textarea>
<script type="module">
  import { WebSocketOpStreamClient } from "./WebSocketOpStreamClient.js";

  const client = new WebSocketOpStreamClient("ws://localhost:8080/collab-ws");
  const join = await client.connectAndJoinAsync("doc-1", "text");

  // Remote edits from other tabs/users arrive here…
  client.onReceiveOp = (payload, newRevision) => { /* apply to the textarea */ };
  // …and you send local edits with client.sendOpAsync("doc-1", op, join.revision)
</script>
```

Open that page in two tabs and they're editing the same document. The full,
runnable version (editing wired both ways, with live cursors) is in the
[HTML + JS samples](https://github.com/OpStreamCollab/OpStream/tree/main/samples) —
or follow the quickstart:

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

- **[Choose your pieces](engines/index.md)** — engines, transports, storage.
- **[Run in production](operations/backplane.md)** — scaling, auth, deploy.
- **[How it works](concepts/document.md)** — the internal model, lifecycle, and
  wire protocol, for contributors and the deeply curious.
