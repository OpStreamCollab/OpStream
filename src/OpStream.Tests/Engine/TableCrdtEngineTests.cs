using FluentAssertions;
using OpStream.Server.Engine.Table;
using System.Text.Json;
using Xunit;

namespace OpStream.Tests.Engine;

/// <summary>
/// Adversarial edge-case tests for <see cref="TableCrdtEngine"/>.
/// </summary>
public class TableCrdtEngineTests
{
    private readonly TableCrdtEngine _engine = new();

    private static JsonElement Str(string s)
    {
        using var doc = JsonDocument.Parse($"\"{s}\"");
        return doc.RootElement.Clone();
    }

    private static JsonElement Obj() => Str("{}");

    /// <summary>
    /// Smoke: a basic insert + set + read round-trip works.
    /// </summary>
    [Fact]
    public void Apply_InsertRowAndSetCell_ProducesExpectedState()
    {
        var batch = TableOpBatch.Create(
            new InsertRowOp("R1", "m", 1, "p"),
            new InsertColumnOp("C1", "m", Obj(), 1, "p"),
            new SetCellOp("R1", "C1", Str("hello"), 2, "p"));

        var state = _engine.Apply(new TableDocument(), batch);

        state.Rows.Should().ContainKey("R1");
        state.Columns.Should().ContainKey("C1");
        state.Cells[new CellAddress("R1", "C1")].Value.GetString().Should().Be("hello");
    }

    /// <summary>
    /// Bug hypothesis: <c>Invert</c> of <see cref="InsertRowOp"/> on a row that already
    /// existed in <paramref name="preState"/> emits a <see cref="MoveRowOp"/> restoring
    /// the previous position — but ignores the tombstone state. If the pre-state row was
    /// tombstoned, the inverse should re-tombstone it; currently it only restores position
    /// while leaving the row visible.
    /// <para>
    /// Concretely:
    /// <list type="number">
    ///   <item>Initial: R1 exists, IsDeleted=true (e.g. concurrent RemoveRowOp won the LWW).</item>
    ///   <item>Apply: InsertRowOp(R1, position="new", ts higher) — wins position LWW, doesn't touch deletion.</item>
    ///   <item>Undo: caller invokes <c>Invert(batch, preState)</c> and applies the result.</item>
    /// </list>
    /// Expected: after applying the inverse the row's <c>IsDeleted</c> should be true again,
    /// matching pre-state. Current implementation leaves <c>IsDeleted = false</c>.
    /// </para>
    /// </summary>
    [Fact]
    public void Invert_InsertRow_OnPreExistingTombstonedRow_ShouldRestoreTombstone()
    {
        // Build pre-state with a tombstoned R1.
        var preState = _engine.Apply(new TableDocument(),
            TableOpBatch.Create(
                new InsertRowOp("R1", "old", 1, "p"),
                new RemoveRowOp("R1", 2, "p")));
        preState.Rows["R1"].IsDeleted.Should().BeTrue();

        // Op: another peer "inserts" R1 with a new position at a later timestamp.
        var op = TableOpBatch.Create(new InsertRowOp("R1", "new", 10, "p2"));
        var postState = _engine.Apply(preState, op);

        // Generate the inverse and apply it on top of postState — this is the round-trip
        // the UndoRedoEngine performs.
        var inverse = _engine.Invert(op, preState);
        var restored = _engine.Apply(postState, inverse);

        restored.Rows["R1"].IsDeleted.Should().BeTrue(
            "round-tripping a batch through Invert+Apply must yield the pre-state's tombstone");
    }

    /// <summary>
    /// LWW tie-break by peerId is REQUIRED for convergence across replicas with the same timestamp.
    /// </summary>
    [Fact]
    public void Apply_SameTimestampDifferentPeers_ResolvesDeterministicallyByPeerIdOrdinal()
    {
        var s1 = _engine.Apply(new TableDocument(), TableOpBatch.Create(
            new SetCellOp("R1", "C1", Str("alice"), 100, "aaa")));
        var s2 = _engine.Apply(s1, TableOpBatch.Create(
            new SetCellOp("R1", "C1", Str("bob"), 100, "zzz")));

        // "zzz" > "aaa" ordinal → zzz wins.
        s2.Cells[new CellAddress("R1", "C1")].Value.GetString().Should().Be("bob");
    }

    /// <summary>
    /// <c>RestampToWin</c> must guarantee the produced batch beats every existing LWW
    /// timestamp in <paramref name="currentState"/>. This is what UndoRedoEngine relies on.
    /// </summary>
    [Fact]
    public void RestampToWin_ProducedOpsBeatEveryExistingTimestamp()
    {
        // Build a state where the maximum timestamp is well above "now" so we exercise
        // the max() branch rather than the wall-clock branch.
        long farFutureTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1_000_000;
        var state = _engine.Apply(new TableDocument(), TableOpBatch.Create(
            new SetCellOp("R1", "C1", Str("future"), farFutureTs, "p")));

        var ancientInverse = TableOpBatch.Create(
            new SetCellOp("R1", "C1", Str("undo"), 1, "p"));

        var restamped = _engine.RestampToWin(ancientInverse, state);

        // Apply the restamped batch; it MUST overwrite "future".
        var after = _engine.Apply(state, restamped);
        after.Cells[new CellAddress("R1", "C1")].Value.GetString().Should().Be("undo");
    }
}
