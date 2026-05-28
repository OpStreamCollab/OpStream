using OpStream.Constants;
using OpStream.Server.Models;
using System.Text.Json;

namespace OpStream.Server.Engine.Tree;

/// <summary>
/// Strongly-typed facade over <see cref="TreeCrdtEngine"/>. Callers work with a domain
/// payload <typeparamref name="TPayload"/> (e.g. <c>BlockContent</c>, <c>FileMeta</c>) and
/// the wrapper serializes to <see cref="JsonElement"/> at the engine boundary.
/// <para>
/// Mirrors the awareness untyped + typed pattern: there is exactly one runtime engine —
/// the untyped core — so the wire format, persistence, and transports stay uniform; the
/// typed layer is a thin convenience on top.
/// </para>
/// </summary>
public sealed class TreeCrdtEngine<TPayload> : IOpEngine<TreeDocument, TreeOpBatch>
{
    private readonly TreeCrdtEngine _inner = new();
    private readonly JsonSerializerOptions _jsonOptions;

    public TreeCrdtEngine(JsonSerializerOptions? jsonOptions = null)
    {
        _jsonOptions = jsonOptions ?? OpStreamJsonOptions.Default;
    }

    public TreeDocument Apply(TreeDocument state, TreeOpBatch op) => _inner.Apply(state, op);
    public TreeOpBatch? Transform(TreeOpBatch incoming, TreeOpBatch existing, TransformPriority priority) => _inner.Transform(incoming, existing, priority);
    public TreeOpBatch? Compose(TreeOpBatch a, TreeOpBatch b) => _inner.Compose(a, b);
    public TreeOpBatch Invert(TreeOpBatch op, TreeDocument preState) => _inner.Invert(op, preState);
    public bool IsNoOp(TreeOpBatch op) => _inner.IsNoOp(op);
    // RestampToWin: inherits the IOpEngine default (identity). TreeCrdtEngine's move-log
    // algorithm tolerates stale timestamps natively, so no override is needed.

    /// <summary>
    /// Builds a <see cref="MoveOp"/> with <typeparamref name="TPayload"/> serialized
    /// to <see cref="JsonElement"/>. Pure helper — does not mutate any state.
    /// </summary>
    public MoveOp BuildMove(string nodeId, string newParentId, string newPosition, TPayload payload, long timestamp, string peerId)
    {
        var element = JsonSerializer.SerializeToElement(payload, _jsonOptions);
        return new MoveOp(nodeId, newParentId, newPosition, element, timestamp, peerId);
    }

    /// <summary>
    /// Reads a node's payload back into <typeparamref name="TPayload"/>.
    /// </summary>
    public TPayload? ReadPayload(TreeNode node) => node.Payload.Deserialize<TPayload>(_jsonOptions);
}
