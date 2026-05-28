# OpStream documentation site

This folder hosts the MkDocs project that builds the OpStream
documentation site.

## Local preview

```bash
pip install mkdocs-material
mkdocs serve -f mkdocs/mkdocs.yml
```

Then open [http://127.0.0.1:8000](http://127.0.0.1:8000).

## Build

```bash
mkdocs build -f mkdocs/mkdocs.yml -d site
```

The static site lands in `mkdocs/site/`.

## Layout

```
mkdocs/
├── mkdocs.yml            # Site configuration + nav
└── docs/
    ├── index.md          # Landing page
    ├── getting-started/  # Installation, quickstart, concepts
    ├── engines/          # Per-engine pages
    ├── transports/       # SignalR / WebSockets / gRPC
    ├── storage/          # Storage backend pages
    ├── operations/       # Backplane, auth, multitenancy, observability, snapshots, deployment
    ├── recipes/          # End-to-end walkthroughs
    └── reference/        # Builder API, engine contracts, wire protocol
```

## Contributing

- Use Material's admonitions / tabs / annotations for clarity.
- Code samples must reference real, current public APIs — don't invent.
- Keep each page focused on a single concern and link to related pages.
