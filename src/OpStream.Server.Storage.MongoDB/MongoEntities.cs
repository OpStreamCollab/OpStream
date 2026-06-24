using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System.Text.Json;

namespace OpStream.Server.Storage.MongoDB;

internal class MongoDocumentOp
{
    [BsonId]
    public string Id { get; set; } = null!; // documentId:revision
    public string DocumentId { get; set; } = null!;
    public long Revision { get; set; }
    public string AuthorId { get; set; } = null!;
    public DateTime Timestamp { get; set; }
    public byte[] Payload { get; set; } = null!;
    public string EngineType { get; set; } = null!;
}

internal class MongoDocumentSnapshot
{
    [BsonId]
    public string DocumentId { get; set; } = null!;
    public long Revision { get; set; }
    public DateTime Timestamp { get; set; }
    public byte[] State { get; set; } = null!;
}

internal class MongoHistoryOp
{
    [BsonId]
    public string Id { get; set; } = null!; // documentId:revision
    public string DocumentId { get; set; } = null!;
    public long Revision { get; set; }
    public string AuthorId { get; set; } = null!;
    public DateTime Timestamp { get; set; }
    public byte[] Payload { get; set; } = null!;
    public string EngineType { get; set; } = null!;
}

internal class MongoHistorySnapshot
{
    [BsonId]
    public ObjectId Id { get; set; }
    public string DocumentId { get; set; } = null!;
    public long Revision { get; set; }
    public DateTime Timestamp { get; set; }
    public byte[] State { get; set; } = null!;
    public string? Name { get; set; }
}

internal class MongoComment
{
    [BsonId]
    public string Id { get; set; } = null!;
    public string DocumentId { get; set; } = null!;
    public string? ParentCommentId { get; set; }
    public string AuthorPeerId { get; set; } = null!;
    public string AuthorName { get; set; } = "";
    public string Body { get; set; } = null!;
    /// <summary>JSON-serialised Anchor, or null for replies.</summary>
    public string? AnchorJson { get; set; }
    public long AnchoredAtRevision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolvedByPeerId { get; set; }
    public bool IsOrphaned { get; set; }
}
