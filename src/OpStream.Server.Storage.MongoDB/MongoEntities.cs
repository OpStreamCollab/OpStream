using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

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
