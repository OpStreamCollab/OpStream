using OpStream.Server.Models;

namespace OpStream.Server.Transports.WebSockets;

/// <summary>
/// Incoming management command sent by a management client over WebSockets.
/// </summary>
public class WebSocketMgmtRequest
{
    public string? CorrelationId { get; set; }

    /// <summary>One of the <see cref="OpStream.Constants.OpStreamConstants.ManagementHubMethods"/> constants.</summary>
    public required string Command { get; set; }

    // ─── Optional parameters (populated depending on Command) ────────────────
    public int? Skip { get; set; }
    public int? Take { get; set; }
    public string? DocumentId { get; set; }
    public long? UpToRevision { get; set; }
}

/// <summary>
/// Server response to a management command.
/// </summary>
public class WebSocketMgmtResponse
{
    public string? CorrelationId { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }

    // ─── Typed payloads (at most one is non-null per response) ───────────────
    public IReadOnlyList<DocumentInfo>? Documents { get; set; }
    public DocumentInfo? DocumentInfo { get; set; }
    public DocumentSnapshot? Snapshot { get; set; }
    public IReadOnlyList<HistoryMilestone>? Milestones { get; set; }
    public int? Count { get; set; }
}
