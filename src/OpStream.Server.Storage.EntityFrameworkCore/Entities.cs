using System.ComponentModel.DataAnnotations;

namespace OpStream.Server.Storage.EntityFrameworkCore;

/// <summary>
/// Stores a comment (root or reply) together with its optional JSON-serialised anchor.
/// </summary>
public class CommentEntity
{
    [Key]
    public string Id { get; set; } = null!;
    public string DocumentId { get; set; } = null!;
    public string? ParentCommentId { get; set; }
    public string AuthorPeerId { get; set; } = null!;
    public string Body { get; set; } = null!;
    /// <summary>JSON-serialised <c>Anchor</c>, or null for reply comments.</summary>
    public string? AnchorJson { get; set; }
    public long AnchoredAtRevision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolvedByPeerId { get; set; }
    public bool IsOrphaned { get; set; }
}

/// <summary>
/// Represents a condensed state of a document in the database for fast loading.
/// </summary>
public class DocumentSnapshotEntity
{
    [Key]
    public string DocumentId { get; set; } = null!;
    public long Revision { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public byte[] State { get; set; } = null!;
}

/// <summary>
/// Represents a single operation in the immutable log for a document.
/// </summary>
public class DocumentOpEntity
{
    public string DocumentId { get; set; } = null!;
    public long Revision { get; set; }
    public string AuthorId { get; set; } = null!;
    public DateTimeOffset Timestamp { get; set; }
    public byte[] Payload { get; set; } = null!;
    public string EngineType { get; set; } = null!;
}

/// <summary>
/// Represents a single operation in the cold storage (permanent history).
/// </summary>
public class HistoryOpEntity
{
    public string DocumentId { get; set; } = null!;
    public long Revision { get; set; }
    public string AuthorId { get; set; } = null!;
    public DateTimeOffset Timestamp { get; set; }
    public byte[] Payload { get; set; } = null!;
    public string EngineType { get; set; } = null!;
}

/// <summary>
/// Represents a historical snapshot taken at a specific revision.
/// </summary>
public class HistorySnapshotEntity
{
    public string DocumentId { get; set; } = null!;
    public long Revision { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public byte[] State { get; set; } = null!;
    public string? Name { get; set; }
}

// ─── Versioning ref registry ──────────────────────────────────────────────────

/// <summary>
/// Registers a stable human-readable name and its default branch.
/// </summary>
public class DocumentNameEntity
{
    [Key]
    public string GlobalName { get; set; } = null!;
    public string DefaultBranchId { get; set; } = null!;
    public string EngineType { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Metadata for one branch (line of history). The physical op-log key is <see cref="PhysicalDocumentId"/>.
/// </summary>
public class DocumentBranchEntity
{
    public string GlobalName { get; set; } = null!;
    public string BranchId { get; set; } = null!;
    public string PhysicalDocumentId { get; set; } = null!;
    public string? ForkParentBranchId { get; set; }
    public long ForkRevision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool IsReadOnly { get; set; }
}

/// <summary>
/// Immutable version tag: a named pointer to a specific revision on a branch.
/// </summary>
public class DocumentVersionEntity
{
    public string GlobalName { get; set; } = null!;
    public string BranchId { get; set; } = null!;
    public string Tag { get; set; } = null!;
    public long Revision { get; set; }
    /// <summary>Name of the backing named milestone in <c>HistorySnapshots</c>.</summary>
    public string HistorySnapshotName { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}
