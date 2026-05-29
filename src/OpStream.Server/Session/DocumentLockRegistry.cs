using System.Collections.Concurrent;

namespace OpStream.Server.Session;

/// <summary>
/// Provides per-document async mutual exclusion. Centralizes the keyed-semaphore pattern that
/// session and awareness creation rely on, so the create-if-absent / acquire / release dance
/// lives in exactly one place.
/// </summary>
public interface IDocumentLockRegistry
{
    /// <summary>
    /// Acquires the lock for <paramref name="documentId"/>, creating it on first use.
    /// Dispose the returned handle to release.
    /// </summary>
    Task<IDisposable> AcquireAsync(string documentId, CancellationToken ct = default);

    /// <summary>
    /// Forgets the lock for a document once it is fully closed. Safe to call if absent.
    /// </summary>
    void Remove(string documentId);
}

/// <inheritdoc />
public sealed class DocumentLockRegistry : IDocumentLockRegistry
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    /// <inheritdoc />
    public async Task<IDisposable> AcquireAsync(string documentId, CancellationToken ct = default)
    {
        var gate = _locks.GetOrAdd(documentId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Releaser(gate);
    }

    /// <inheritdoc />
    public void Remove(string documentId)
    {
        // Intentionally not disposed: we never touch AvailableWaitHandle, so a SemaphoreSlim
        // needs no disposal, and not disposing removes the "released after dispose" hazard the
        // previous inline implementation had.
        _locks.TryRemove(documentId, out _);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose()
        {
            var gate = Interlocked.Exchange(ref _gate, null);
            gate?.Release();
        }
    }
}
