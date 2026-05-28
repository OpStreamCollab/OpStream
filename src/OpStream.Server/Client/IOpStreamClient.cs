namespace OpStream.Server.Client;

/// <summary>
/// Results of the initial handshake.
/// </summary>
public record ClientJoinResult(long Revision, ReadOnlyMemory<byte> Snapshot);

/// <summary>
/// Results of a client operation submission.
/// </summary>
public record ClientOpResult(bool Success, long NewRevision, string? ErrorMessage);

/// <summary>
/// Transport abstraction so that UI clients do not depend on SignalR.
/// </summary>
public interface IOpStreamClient : IAsyncDisposable
{
    /// <summary>
    /// Event triggered when a remote operation arrives.
    /// Func(ReadOnlyMemory<byte> payload, long newRevision)
    /// </summary>
    event Func<ReadOnlyMemory<byte>, long, Task>? OnReceiveOp;

    /// <summary>
    /// Event triggered if the connection is lost.
    /// </summary>
    event Action<Exception?>? OnDisconnected;

    /// <summary>
    /// Starts the connection and performs the handshake with the document.
    /// </summary>
    Task<ClientJoinResult> ConnectAndJoinAsync(string documentId, CancellationToken ct = default);

    /// <summary>
    /// Sends a local operation to the server.
    /// </summary>
    Task<ClientOpResult> SendOpAsync(string documentId, ReadOnlyMemory<byte> payload, long baseRevision, CancellationToken ct = default);
}