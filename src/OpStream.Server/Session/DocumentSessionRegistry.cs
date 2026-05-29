using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpStream.Server.Storage;

namespace OpStream.Server.Session;

/// <summary>
/// Tunables for the in-memory session registry.
/// </summary>
public sealed class SessionRegistryOptions
{
    /// <summary>
    /// How long a document keeps its in-memory session alive after the last peer leaves,
    /// before it is closed. Defaults to 5 minutes.
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Owns the map of live <see cref="IDocumentSession"/> instances on this node and knows how to
/// open one (load the latest snapshot, replay subsequent ops) and dispose it. It is the single
/// authority for "is this document live here, and how do I get its session".
/// </summary>
public interface IDocumentSessionRegistry
{
    /// <summary>Returns the live session if one exists on this node; does not open one.</summary>
    IDocumentSession? TryGet(string documentId);

    /// <summary>Returns the live session, opening (snapshot + replay) it if necessary.</summary>
    Task<IDocumentSession> GetOrOpenAsync(string documentId, string documentType, CancellationToken ct = default);

    /// <summary>Disposes and removes the session for the document. Safe if absent.</summary>
    Task CloseAsync(string documentId);

    /// <summary>Ids of every document with a live session on this node.</summary>
    IReadOnlyList<string> ActiveDocumentIds { get; }
}

/// <inheritdoc />
public sealed class DocumentSessionRegistry : IDocumentSessionRegistry
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDocumentLockRegistry _locks;
    private readonly ILogger<DocumentSessionRegistry> _logger;
    private readonly Dictionary<string, IDocumentSessionFactory> _factories;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IDocumentSession> _sessions = new();

    public DocumentSessionRegistry(
        IServiceScopeFactory scopeFactory,
        IEnumerable<IDocumentSessionFactory> factories,
        IDocumentLockRegistry locks,
        ILogger<DocumentSessionRegistry> logger)
    {
        _scopeFactory = scopeFactory;
        _locks = locks;
        _logger = logger;
        _factories = factories.ToDictionary(f => f.DocumentType, f => f, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ActiveDocumentIds => _sessions.Keys.ToArray();

    /// <inheritdoc />
    public IDocumentSession? TryGet(string documentId)
        => _sessions.TryGetValue(documentId, out var session) ? session : null;

    /// <inheritdoc />
    public async Task<IDocumentSession> GetOrOpenAsync(string documentId, string documentType, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(documentId, out var existing))
            return existing;

        using (await _locks.AcquireAsync(documentId, ct))
        {
            if (_sessions.TryGetValue(documentId, out var session))
                return session;

            if (!_factories.TryGetValue(documentType, out var factory))
                throw new NotSupportedException($"No session engine registered for document type: {documentType}");

            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

            var snapshot = await store.LoadSnapshotAsync(documentId, ct);
            long currentRevision = snapshot?.Revision ?? 0;

            var newSession = await factory.CreateSessionAsync(documentId, currentRevision, snapshot?.State, ct);

            await foreach (var storedOp in store.StreamOpsAsync(documentId, currentRevision, ct))
                await newSession.RehydrateOpAsync(storedOp);

            _sessions.TryAdd(documentId, newSession);
            return newSession;
        }
    }

    /// <inheritdoc />
    public async Task CloseAsync(string documentId)
    {
        if (_sessions.TryRemove(documentId, out var session))
        {
            _logger.LogInformation("Closing session for document {DocId}", documentId);
            await session.DisposeAsync();
        }
    }
}
