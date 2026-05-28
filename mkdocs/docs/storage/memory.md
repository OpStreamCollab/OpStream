# In-memory storage

The fallback registered by `AddOpStream()`. Useful for tests, demos, and
local iteration. **Not for production** — all data is lost when the
process restarts.

## When to use

- Unit tests.
- Local development of a new client.
- Spike / proof-of-concept.

## Setup

It's the default, but you can be explicit:

```csharp
services.AddOpStream()
    .UseMemoryStorage();
```

`UseMemoryStorage()` is the same call you'd use to switch back during
test setup if some other backend was previously registered.

## Behavior

- Op log lives in a `ConcurrentDictionary<string, List<StoredOp>>`.
- Snapshots live in a sibling dictionary.
- Both are cleared when the host process restarts.

## Startup warning

The router logs a **warning** on startup when this backend is still in
use:

```
OpStream is using MemoryDocumentStore. All document data will be lost
when the process restarts. Call UseRedisStorage(), UseEfCoreStorage(),
or another persistent store before going to production.
```

That's the signal to plug in a real backend.
