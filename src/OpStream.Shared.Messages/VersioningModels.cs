namespace OpStream.Shared.Messages;

/// <summary>
/// Registers a stable human-readable name for a document and its default branch.
/// </summary>
public record DocumentNameInfo(
    string Name,
    string DefaultBranchId,
    string EngineType,
    DateTimeOffset CreatedAt);

/// <summary>
/// Metadata for one named line of history (a branch). The physical op-log key is
/// <see cref="PhysicalDocumentId"/>; all engine/transport operations use that id directly.
/// </summary>
public record BranchRef(
    string Name,
    string BranchId,
    string PhysicalDocumentId,
    string? ForkParentBranchId,
    long ForkRevision,
    DateTimeOffset CreatedAt,
    bool IsReadOnly);

/// <summary>
/// An immutable pointer to a specific revision on a branch. Backed by a named history
/// milestone so the snapshot bytes survive compaction.
/// </summary>
public record VersionRef(
    string Name,
    string BranchId,
    string Tag,
    long Revision,
    string HistorySnapshotName,
    DateTimeOffset CreatedAt);

/// <summary>
/// Summary produced by a merge (dry-run or committed).
/// </summary>
public record MergeReport(
    string SourceBranchId,
    string TargetBranchId,
    int RebasedOpCount,
    int NullifiedOpCount,
    bool IsDryRun);
