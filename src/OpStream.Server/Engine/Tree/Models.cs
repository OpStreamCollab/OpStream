using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpStream.Server.Engine.Tree;

/// <summary>
/// Reserved identifiers in the tree. <see cref="RootId"/> is the implicit parent of
/// every top-level node; <see cref="TrashId"/> is the tombstone parent used to model
/// deletion as a move (so the move log stays uniform and undo is symmetric).
/// </summary>
public static class TreeConstants
{
    public const string RootId = "__root__";
    public const string TrashId = "__trash__";
}

/// <summary>
/// A live node in the tree.
/// </summary>
/// <param name="Id">Globally unique node id (peer-generated, e.g. ULID).</param>
/// <param name="ParentId">The node's current parent — may be <see cref="TreeConstants.RootId"/> or <see cref="TreeConstants.TrashId"/>.</param>
/// <param name="Position">
/// Lexicographically comparable sibling ordering key. v1 uses simple fractional indexing
/// (see <c>FractionalIndex</c>); future versions may replace this with a sequence CRDT
/// (RGA / LSEQ) without changing the wire shape — the field stays a string.
/// </param>
/// <param name="Payload">Opaque per-node payload.</param>
public record TreeNode(string Id, string ParentId, string Position, JsonElement Payload);

/// <summary>
/// The complete state for the tree document.
/// </summary>
/// <param name="Nodes">All known nodes, keyed by id. Includes nodes whose parent is <see cref="TreeConstants.TrashId"/>.</param>
/// <param name="MoveLog">
/// Every move that has been applied to reach the current <paramref name="Nodes"/>, sorted by
/// <c>(Timestamp ascending, PeerId ascending)</c>. Required by Kleppmann's move-tree CRDT to
/// undo / redo when a concurrent move arrives out of order.
/// </param>
public record TreeDocument(
    IReadOnlyDictionary<string, TreeNode> Nodes,
    IReadOnlyList<AppliedMove> MoveLog)
{
    public TreeDocument() : this(new Dictionary<string, TreeNode>(), Array.Empty<AppliedMove>()) { }
}

/// <summary>
/// One entry of the move log: the operation that was applied and the per-node state
/// it replaced. <see cref="OldNode"/> is <c>null</c> when the move created the node.
/// Used to reverse-apply moves during the undo/redo dance.
/// </summary>
public record AppliedMove(MoveOp Op, TreeNode? OldNode);

/// <summary>
/// Tree operation. Only one variant exists: every structural change is modelled as a Move
/// (creation = move from limbo, deletion = move to <see cref="TreeConstants.TrashId"/>,
/// reorder = move within the same parent with a new position).
/// </summary>
[JsonDerivedType(typeof(MoveOp), "move")]
public abstract record TreeOp;

/// <summary>
/// Sets a node's parent / position / payload as of <paramref name="Timestamp"/>.
/// Ties on timestamp are broken by <paramref name="PeerId"/> to keep ordering total
/// and consistent across replicas.
/// </summary>
public record MoveOp(
    string NodeId,
    string NewParentId,
    string NewPosition,
    JsonElement NewPayload,
    long Timestamp,
    string PeerId) : TreeOp;

/// <summary>
/// Bundle of tree ops sent atomically (mirrors <c>JsonOpBatch</c>).
/// </summary>
public record TreeOpBatch(IReadOnlyList<TreeOp> Operations)
{
    public static TreeOpBatch Create(params TreeOp[] ops) => new(ops);
}
