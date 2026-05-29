using OpStream.Server.Models;
using OpStream.Server.Versioning;

namespace OpStream.Server.Transports.WebSockets;

/// <summary>
/// Incoming versioning command sent by a client over WebSockets.
/// </summary>
public class WebSocketVerRequest
{
    public string? CorrelationId { get; set; }

    /// <summary>One of the <see cref="OpStream.Constants.OpStreamConstants.VersioningHubMethods"/> constants.</summary>
    public required string Command { get; set; }

    // ─── Common optional parameters ──────────────────────────────────────────
    public string? Name          { get; set; }
    public string? BranchId      { get; set; }
    public string? FromBranchId  { get; set; }
    public string? NewBranchId   { get; set; }
    public string? Tag           { get; set; }
    public string? PhysicalDocumentId { get; set; }
    public string? EngineType    { get; set; }
    public string? RootBranchId  { get; set; }
    public string? TargetBranchId { get; set; }
    public string? SourceBranchId { get; set; }
    public long?   AtRevision    { get; set; }
    public bool    Cascade       { get; set; }
    public bool    DropSnapshot  { get; set; }
    public bool    DryRun        { get; set; }
}

/// <summary>
/// Server response to a versioning command over WebSockets.
/// </summary>
public class WebSocketVerResponse
{
    public string? CorrelationId { get; set; }
    public bool    Success       { get; set; }
    public string? ErrorMessage  { get; set; }

    // ─── Typed payloads (at most one is non-null per response) ───────────────
    public DocumentNameInfo?              NameInfo       { get; set; }
    public IReadOnlyList<DocumentNameInfo>? Names        { get; set; }
    public BranchRef?                     Branch         { get; set; }
    public IReadOnlyList<BranchRef>?      Branches       { get; set; }
    public VersionRef?                    Version        { get; set; }
    public IReadOnlyList<VersionRef>?     Versions       { get; set; }
    public DocumentSnapshot?              Snapshot       { get; set; }
    public MergeReport?                   MergeReport    { get; set; }
}
