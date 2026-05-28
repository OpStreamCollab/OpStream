using OpStream.Server.Models;
using System.Text.Json;

namespace OpStream.Server.Engine.Tree;

/// <summary>
/// Kleppmann move-tree CRDT (untyped core).
/// <para>
/// One operation type — <see cref="MoveOp"/> — captures creation, deletion (move to
/// <see cref="TreeConstants.TrashId"/>), reorder, and reparent. Concurrency is resolved
/// by inserting incoming moves into a timestamp-ordered log and replaying the suffix:
/// undo the moves that come after the insertion point, apply the new move, redo the rest.
/// This guarantees strong eventual consistency for any concurrent permutation of moves
/// — at the cost of carrying a log alongside the live node map.
/// </para>
/// <para>
/// Cycles (moving a node into its own subtree) are detected on each apply and the
/// offending move is recorded as a no-op so it stays in the log for ordering purposes
/// but doesn't mutate the node map.
/// </para>
/// <para>
/// Reference: Martin Kleppmann et al., <i>“A highly-available move operation for
/// replicated trees and distributed filesystems”</i>, 2021.
/// </para>
/// </summary>
public class TreeCrdtEngine : IOpEngine<TreeDocument, TreeOpBatch>
{
    public TreeDocument Apply(TreeDocument state, TreeOpBatch batch)
    {
        var nodes = new Dictionary<string, TreeNode>(state.Nodes);
        var log = new List<AppliedMove>(state.MoveLog);

        foreach (var op in batch.Operations)
        {
            if (op is not MoveOp move) throw new NotSupportedException($"Unsupported tree op: {op.GetType().Name}");

            ApplySingle(nodes, log, move);
        }

        return new TreeDocument(nodes, log);
    }

    public TreeOpBatch? Transform(TreeOpBatch incoming, TreeOpBatch existing, TransformPriority priority)
    {
        // CRDT: concurrent moves are resolved by the timestamp-ordered log inside Apply.
        // No rebase is needed at the transport layer.
        return incoming;
    }

    /// <summary>
    /// Compose is intentionally not supported: combining two MoveOps for the same node
    /// would lose intermediate states and break Invert. Always returns <c>null</c>.
    /// </summary>
    public TreeOpBatch? Compose(TreeOpBatch a, TreeOpBatch b) => null;

    public TreeOpBatch Invert(TreeOpBatch batch, TreeDocument preState)
    {
        var inverted = new List<TreeOp>(batch.Operations.Count);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Walk in reverse so that, when several ops in the batch touch the same node,
        // the undo sequence restores them in the correct order.
        for (int i = batch.Operations.Count - 1; i >= 0; i--)
        {
            if (batch.Operations[i] is not MoveOp move) continue;

            // Look up what the node was before the original op (i.e. in preState).
            if (preState.Nodes.TryGetValue(move.NodeId, out var old))
            {
                long safeTs = Math.Max(now, move.Timestamp + 1);
                inverted.Add(new MoveOp(old.Id, old.ParentId, old.Position, old.Payload, safeTs, move.PeerId));
            }
            else
            {
                // The op created the node — undo by sending it to TRASH.
                long safeTs = Math.Max(now, move.Timestamp + 1);
                inverted.Add(new MoveOp(move.NodeId, TreeConstants.TrashId, move.NewPosition, move.NewPayload, safeTs, move.PeerId));
            }
        }

        return new TreeOpBatch(inverted);
    }

    public bool IsNoOp(TreeOpBatch op) => op.Operations.Count == 0;

    // ── Internals ────────────────────────────────────────────────────────────────────

    private static void ApplySingle(Dictionary<string, TreeNode> nodes, List<AppliedMove> log, MoveOp move)
    {
        // Binary-search the insertion point in the timestamp-ordered log.
        int insertAt = FindInsertIndex(log, move);

        // 1. Undo every move that lives after the insertion point.
        for (int i = log.Count - 1; i >= insertAt; i--)
        {
            UndoMove(nodes, log[i]);
        }

        // 2. Apply the new move, recording its undo data.
        var redoTail = log.GetRange(insertAt, log.Count - insertAt);
        log.RemoveRange(insertAt, log.Count - insertAt);

        var appliedNew = DoMove(nodes, move);
        log.Add(appliedNew);

        // 3. Redo the suffix on top of the new state. Their original undo snapshots are
        //    discarded because the "before" state changed — we capture fresh ones.
        foreach (var entry in redoTail)
        {
            var redone = DoMove(nodes, entry.Op);
            log.Add(redone);
        }
    }

    /// <summary>
    /// Applies a move to the node map, returns the log entry capturing what was overwritten.
    /// If the move would create a cycle, it is recorded but does not mutate the map.
    /// </summary>
    private static AppliedMove DoMove(Dictionary<string, TreeNode> nodes, MoveOp move)
    {
        nodes.TryGetValue(move.NodeId, out var previous);

        if (WouldCreateCycle(nodes, move.NodeId, move.NewParentId))
        {
            // Skip the mutation but keep the entry so the log stays in lockstep with peers.
            return new AppliedMove(move, previous);
        }

        nodes[move.NodeId] = new TreeNode(move.NodeId, move.NewParentId, move.NewPosition, move.NewPayload);
        return new AppliedMove(move, previous);
    }

    /// <summary>Reverse-applies a log entry, restoring whatever the move overwrote.</summary>
    private static void UndoMove(Dictionary<string, TreeNode> nodes, AppliedMove entry)
    {
        if (entry.OldNode is null)
        {
            // The move had created the node — undo removes it entirely.
            nodes.Remove(entry.Op.NodeId);
        }
        else
        {
            nodes[entry.OldNode.Id] = entry.OldNode;
        }
    }

    /// <summary>
    /// Walks the parent chain starting at <paramref name="newParentId"/>. If it reaches
    /// <paramref name="movingNodeId"/> — either through an existing node or through an
    /// orphan ParentId reference (a node whose declared parent isn't present in the map
    /// yet, e.g. because it was removed by an undo step) — applying the move would close
    /// a cycle and we refuse it.
    /// </summary>
    private static bool WouldCreateCycle(Dictionary<string, TreeNode> nodes, string movingNodeId, string newParentId)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var cursor = newParentId;
        while (true)
        {
            if (cursor == TreeConstants.RootId || cursor == TreeConstants.TrashId) return false;
            // Critical: the cursor check must run BEFORE the dict lookup so that orphan
            // ParentId pointers back to movingNode (whose own entry may be absent during
            // an undo/redo replay) are still detected.
            if (cursor == movingNodeId) return true;
            if (!visited.Add(cursor)) return true; // pre-existing cycle, bail
            if (!nodes.TryGetValue(cursor, out var parent)) return false; // end of chain
            cursor = parent.ParentId;
        }
    }

    /// <summary>
    /// Binary search by <c>(Timestamp asc, PeerId asc)</c>. Returns the first index whose
    /// key is &gt; the incoming move's key — i.e. the position the new entry should occupy.
    /// </summary>
    private static int FindInsertIndex(List<AppliedMove> log, MoveOp incoming)
    {
        int lo = 0, hi = log.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (CompareKeys(log[mid].Op, incoming) <= 0) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static int CompareKeys(MoveOp a, MoveOp b)
    {
        int t = a.Timestamp.CompareTo(b.Timestamp);
        if (t != 0) return t;
        return string.CompareOrdinal(a.PeerId, b.PeerId);
    }
}
