using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace OpStream.Server.Engine.Json
{
    /// <summary>
    /// Represents a single register in a CRDT document, tracking its value, 
    /// the timestamp of the last modification, and the peer who made it.
    /// </summary>
    public record CrdtRegister(JsonElement Value, long Timestamp, string PeerId, bool IsDeleted = false);

    /// <summary>
    /// Represents a JSON document as a dictionary of paths (e.g., "root.user.name") mapping to registers.
    /// </summary>
    public record Json_Document(Dictionary<string, CrdtRegister> Registers)
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Json_Document"/> class.
        /// </summary>
        public Json_Document() : this(new Dictionary<string, CrdtRegister>()) { }
    }

    /// <summary>
    /// Base class for JSON operations.
    /// </summary>
    [JsonDerivedType(typeof(SetPropertyOp), "set")]
    [JsonDerivedType(typeof(DeletePropertyOp), "del")]
    public abstract record JsonOp;

    /// <summary>
    /// Operation to set a property at a specific path.
    /// </summary>
    public record SetPropertyOp(string Path, JsonElement Value, long Timestamp, string PeerId) : JsonOp;

    /// <summary>
    /// Operation to delete a property at a specific path.
    /// </summary>
    public record DeletePropertyOp(string Path, long Timestamp, string PeerId) : JsonOp;

    /// <summary>
    /// Represents a batch of JSON operations to be sent or applied together.
    /// </summary>
    public record JsonOpBatch(IReadOnlyList<JsonOp> Operations)
    {
        /// <summary>
        /// Creates an operation batch with the specified operations.
        /// </summary>
        public static JsonOpBatch Create(params JsonOp[] ops) => new(ops);
    }
}
