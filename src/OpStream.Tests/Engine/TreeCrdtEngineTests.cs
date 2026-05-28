using FluentAssertions;
using OpStream.Server.Engine.Tree;
using System.Text.Json;
using Xunit;

namespace OpStream.Tests.Engine;

/// <summary>
/// Adversarial edge-case tests for <see cref="TreeCrdtEngine"/>.
/// These tests are designed to expose suspected bugs in the cycle check and the
/// undo / redo dance around moves of non-existent or transiently-removed nodes.
/// </summary>
public class TreeCrdtEngineTests
{
    private readonly TreeCrdtEngine _engine = new();

    private static JsonElement NullPayload()
    {
        using var doc = JsonDocument.Parse("null");
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Pure smoke: a single Move creates the node and places it.
    /// </summary>
    [Fact]
    public void Apply_SingleMove_CreatesNodeUnderRoot()
    {
        var batch = new TreeOpBatch(new[]
        {
            new MoveOp("A", TreeConstants.RootId, "m", NullPayload(), 100, "peer1")
        });

        var result = _engine.Apply(new TreeDocument(), batch);

        result.Nodes.Should().ContainKey("A");
        result.Nodes["A"].ParentId.Should().Be(TreeConstants.RootId);
    }

    /// <summary>
    /// Bug hypothesis: when a node is transiently removed by the undo step of the
    /// move-log dance and another op pointing to it has already been recorded, the
    /// cycle check walks the parent chain via <c>nodes.TryGetValue</c> and stops at
    /// the missing key — failing to detect that the moving node, once reinserted by
    /// the redo step, will close a cycle with that orphan pointer.
    /// <para>
    /// Scenario:
    /// <list type="number">
    ///   <item>M1 (ts=10): create A under B (B doesn't exist yet — A becomes an orphan-parented node).</item>
    ///   <item>M2 (ts=5, arrives second): create B under A. Lower timestamp → forces an undo of M1, then apply M2, then redo M1.</item>
    /// </list>
    /// After M2 lands and during the redo of M1, the cycle check should refuse to put
    /// A back under B because B's ParentId is now A. The current implementation walks
    /// from B upward, hits the missing-key for "A" (A was removed during undo) and
    /// returns false → cycle closes. The expected behavior is to keep the graph acyclic.
    /// </para>
    /// </summary>
    [Fact]
    public void Apply_OrphanParentPointer_ShouldNotAllowCycleAfterUndoRedoDance()
    {
        // M1 arrives first
        var m1 = new TreeOpBatch(new[]
        {
            new MoveOp("A", "B", "p", NullPayload(), 10, "peer1")
        });
        var afterM1 = _engine.Apply(new TreeDocument(), m1);

        // M2 arrives second but has lower timestamp — triggers the undo/redo of M1
        var m2 = new TreeOpBatch(new[]
        {
            new MoveOp("B", "A", "q", NullPayload(), 5, "peer2")
        });
        var afterM2 = _engine.Apply(afterM1, m2);

        // No cycle: traversing parent pointers from any node must reach RootId / TrashId,
        // not loop back. Concretely: A.parent must not be B while B.parent is A.
        var aParent = afterM2.Nodes.TryGetValue("A", out var a) ? a.ParentId : TreeConstants.RootId;
        var bParent = afterM2.Nodes.TryGetValue("B", out var b) ? b.ParentId : TreeConstants.RootId;
        (aParent == "B" && bParent == "A").Should().BeFalse(
            "the engine must not produce a state where A.parent==B AND B.parent==A");
    }

    /// <summary>
    /// Sanity test that confirms cycle detection works in the simple case (no log-replay involved):
    /// a direct second move that would make A the child of its own descendant must be rejected.
    /// </summary>
    [Fact]
    public void Apply_DirectCycle_IsRejected()
    {
        // A under root, B under A
        var setup = new TreeOpBatch(new[]
        {
            new MoveOp("A", TreeConstants.RootId, "m", NullPayload(), 1, "p"),
            new MoveOp("B", "A", "n", NullPayload(), 2, "p")
        });
        var state = _engine.Apply(new TreeDocument(), setup);

        // Try to move A under B (its own child) — must be rejected.
        var cycleOp = new TreeOpBatch(new[]
        {
            new MoveOp("A", "B", "o", NullPayload(), 3, "p")
        });
        var afterCycle = _engine.Apply(state, cycleOp);

        // A should still be under root.
        afterCycle.Nodes["A"].ParentId.Should().Be(TreeConstants.RootId);
    }
}
