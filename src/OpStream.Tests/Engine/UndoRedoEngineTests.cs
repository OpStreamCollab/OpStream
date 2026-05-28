using FluentAssertions;
using OpStream.Server.Engine;
using OpStream.Server.Engine.UndoRedo;
using OpStream.Server.Models;
using Xunit;

namespace OpStream.Tests.Engine;

/// <summary>
/// Adversarial tests for <see cref="UndoRedoEngine{TDoc,TOp}"/>.
/// Specifically targets the relationship between <c>PrepareUndo</c> (which may
/// return a candidate from a deeper position in the stack when shallower ones are
/// nullified by concurrent edits) and <c>NotifyUndoApplied</c> (which pops the
/// top of the stack unconditionally).
/// </summary>
public class UndoRedoEngineTests
{
    /// <summary>
    /// Toy doc / op pair so we can fully control Transform / Invert / IsNoOp from
    /// a test without depending on a real engine. <see cref="TestOp.NullifiedBy"/>
    /// describes which sequence ids — once recorded into <c>_log</c> after this op —
    /// should nullify the inverse during the rebase loop.
    /// </summary>
    private sealed record TestDoc(int Version);
    private sealed record TestOp(string Tag, long Sequence = 0, int[]? NullifiedBy = null);

    private sealed class ScriptedEngine : IOpEngine<TestDoc, TestOp>
    {
        public TestDoc Apply(TestDoc state, TestOp op) => state with { Version = state.Version + 1 };

        public TestOp? Transform(TestOp incoming, TestOp existing, TransformPriority priority)
        {
            // Nullify the incoming if the existing op's sequence is in incoming.NullifiedBy.
            if (incoming.NullifiedBy is not null &&
                Array.IndexOf(incoming.NullifiedBy, (int)existing.Sequence) >= 0)
            {
                return null;
            }
            return incoming;
        }

        public TestOp? Compose(TestOp a, TestOp b) => null;
        public TestOp Invert(TestOp op, TestDoc preState)
            => new($"inv({op.Tag})", op.Sequence, op.NullifiedBy);
        public bool IsNoOp(TestOp op) => op.Tag.Length == 0;
    }

    [Fact]
    public void Smoke_RecordAndPrepareUndo_ReturnsCachedInverse()
    {
        var engine = new ScriptedEngine();
        var ur = new UndoRedoEngine<TestDoc, TestOp>(engine);
        ur.RecordApplied("peer1", new TestOp("X"), new TestDoc(0), 1);

        var prepared = ur.PrepareUndo("peer1", new TestDoc(1));

        prepared.HasOp.Should().BeTrue();
        prepared.Op!.Tag.Should().Be("inv(X)");
    }

    /// <summary>
    /// Bug hypothesis. Scenario:
    /// <list type="number">
    ///   <item>Peer P records op X (seq=1).</item>
    ///   <item>Peer P records op Y (seq=2). Y's inverse is configured to be nullified
    ///         by any op recorded after it (here: Z below).</item>
    ///   <item>Some other peer records op Z (seq=3).</item>
    ///   <item>P calls <c>PrepareUndo</c>. The engine walks the stack [Y, X]:
    ///         Y's inverse gets Transform'd against Z → returns null → continue.
    ///         X's inverse survives → returned.</item>
    ///   <item>P applies the returned X-inverse → calls <c>NotifyUndoApplied</c>.</item>
    /// </list>
    /// The engine pops the top of the undo stack — which is Y (the nullified one),
    /// NOT X (the one we actually returned). So the redo stack ends up holding Y,
    /// and a subsequent Redo would "redo" Y (which was never undone), while the
    /// X entry stays on the undo stack as if untouched.
    /// </summary>
    [Fact]
    public void NotifyUndoApplied_MustMoveTheEntryActuallyReturned_NotJustTheTopOfStack()
    {
        var engine = new ScriptedEngine();
        var ur = new UndoRedoEngine<TestDoc, TestOp>(engine);

        // Sequence ids on TestOp mirror the engine's internal sequence so the scripted
        // Transform can match by existing.Sequence (the engine assigns 1, 2, 3 in order).
        var seqX = ur.RecordApplied("peer1", new TestOp("X", Sequence: 1, NullifiedBy: null), new TestDoc(0), 1);
        var seqY = ur.RecordApplied("peer1", new TestOp("Y", Sequence: 2, NullifiedBy: new[] { 3 }), new TestDoc(1), 2);
        var seqZ = ur.RecordApplied("peer2", new TestOp("Z", Sequence: 3, NullifiedBy: null), new TestDoc(2), 3);

        // PrepareUndo for peer1 must skip Y (nullified) and return X's inverse.
        var prepared = ur.PrepareUndo("peer1", new TestDoc(3));
        prepared.HasOp.Should().BeTrue("X's inverse should survive");
        prepared.Op!.Tag.Should().Be("inv(X)", "Y was nullified, so X is the candidate");

        // Caller applies the inverse and notifies — echoing back the sequence id that
        // PrepareUndo reported as consumed.
        ur.NotifyUndoApplied("peer1", prepared.ConsumedSequence);

        // After notify:
        // - The entry actually "consumed" was X (seq=1). Redo should resurrect X, not Y.
        // - Undo should no longer offer X (already undone). It may still offer Y if Y
        //   ever becomes prepareable in the future (it stays alive in the log).
        var redoPrepared = ur.PrepareRedo("peer1", new TestDoc(4));
        redoPrepared.HasOp.Should().BeTrue();
        redoPrepared.Op!.Tag.Should().Be("X",
            "redo must replay the op whose inverse was actually applied; got \"{0}\"",
            redoPrepared.Op.Tag);
    }
}
