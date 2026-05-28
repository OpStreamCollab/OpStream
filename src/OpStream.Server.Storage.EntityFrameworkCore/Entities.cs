using System.ComponentModel.DataAnnotations;

namespace OpStream.Server.Storage.EntityFrameworkCore;

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
