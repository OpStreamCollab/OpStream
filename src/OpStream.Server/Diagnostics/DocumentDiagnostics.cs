namespace OpStream.Server.Diagnostics;

/// <summary>
/// Snapshot of a document session for the diagnostics endpoint
/// (<c>GET /opstream/diag/{docId}</c>).
/// </summary>
public sealed record DocumentDiagnostics(
    string DocumentId,
    bool ActiveOnThisNode,
    string? OwnerNodeId,
    long Revision,
    int PeerCount,
    IReadOnlyList<string> Peers,
    IReadOnlyList<DiagnosticsOpEntry> RecentOps);

/// <summary>One entry in the recent-ops tail returned by the diagnostics endpoint.</summary>
public sealed record DiagnosticsOpEntry(
    long Revision,
    string AuthorId,
    DateTimeOffset Timestamp,
    int PayloadBytes,
    string EngineType);
