using OpStream.Server.Models;
using OpStream.Shared.Abstractions;
using OpStream.Shared.Messages;
using System.Text.Json;


namespace OpStream.Server.Session;





/// <summary>
/// Represents the in-memory state of an active document on the server.
/// </summary>
public interface IDocumentSession : IAsyncDisposable
{
    /// <summary>
    /// Gets the unique identifier for the document.
    /// </summary>
    string DocumentId { get; }

    /// <summary>
    /// Gets the current revision number of the document.
    /// </summary>
    long CurrentRevision { get; }
    
    /// <summary>
    /// Gets the number of currently active peers in the session.
    /// </summary>
    int ActivePeersCount { get; }

    /// <summary>
    /// Enumerates the IDs of the peers currently connected to this session.
    /// Returned snapshot is consistent at the moment of the call.
    /// </summary>
    IReadOnlyCollection<string> Peers { get; }

    /// <summary>
    /// Connects a client, returning the current state and revision to sync.
    /// </summary>
    Task<DocumentStateResult> JoinAsync(string peerId, CancellationToken ct = default);
    
    /// <summary>
    /// Disconnects a client from the session.
    /// </summary>
    Task LeaveAsync(string peerId, CancellationToken ct = default);

    /// <summary>
    /// Receives an operation from a client, transforms it against pending ops, 
    /// applies it, persists it, and broadcasts it to other peers.
    /// </summary>
    Task<OpApplyResult> ApplyOpAsync(string peerId, ReadOnlyMemory<byte> payload, long baseRevision, CancellationToken ct = default);

    Task RehydrateOpAsync(StoredOp storedOp);

    /// <summary>
    /// Runs <paramref name="action"/> while holding the session's serialization lock.
    /// Used by side-effects that need an atomic snapshot of <see cref="CurrentRevision"/>
    /// — for example, anchoring a new comment at exactly the revision the document is at
    /// when the anchor is computed.
    /// <para>
    /// Keep the action cheap: it blocks every op from being applied until it completes.
    /// </para>
    /// </summary>
    Task<T> ExecuteUnderLockAsync<T>(Func<long, ValueTask<T>> action, CancellationToken ct = default);
}

/// <summary>
/// Represents the result of a client joining a session for the document state.
/// </summary>
public record DocumentStateResult(
    long Revision,
    ReadOnlyMemory<byte> Snapshot,
    IEnumerable<ReadOnlyMemory<byte>> PendingOps);

/// <summary>
/// Represents the result of a client joining a session.
/// </summary>
public record SessionJoinResult(
    long Revision,
    ReadOnlyMemory<byte> Snapshot,
    IEnumerable<ReadOnlyMemory<byte>> PendingOps,
    List<AwarenessState> CurrentAwareness);

/// <summary>
/// Represents the result of applying an operation to a document.
/// </summary>
public record OpApplyResult(
    bool Success, 
    long NewRevision, 
    string? ErrorMessage = null);
