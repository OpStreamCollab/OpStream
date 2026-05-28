using FluentAssertions;
using OpStream.Server.Engine;
using OpStream.Server.Engine.Json;
using OpStream.Server.Models;
using System.Text.Json;
using Xunit;

namespace OpStream.Tests.Engine;

/// <summary>
/// Unit tests for the JsonCrdtEngine class.
/// </summary>
public class JsonCrdtEngineTests
{
    private readonly JsonCrdtEngine _engine;

    public JsonCrdtEngineTests()
    {
        _engine = new JsonCrdtEngine();
    }

    private JsonElement CreateJsonElement(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private JsonElement CreateJsonString(string value)
    {
        return CreateJsonElement($"\"{value}\"");
    }

    [Fact]
    public void Apply_SetProperty_ShouldAddOrUpdateRegister()
    {
        var doc = new Json_Document();
        var val = CreateJsonString("Alice");
        var op = JsonOpBatch.Create(new SetPropertyOp("user.name", val, 100, "peer1"));

        var newState = _engine.Apply(doc, op);

        newState.Registers.Should().ContainKey("user.name");
        var reg = newState.Registers["user.name"];
        reg.Value.GetString().Should().Be("Alice");
        reg.Timestamp.Should().Be(100);
        reg.PeerId.Should().Be("peer1");
    }

    [Fact]
    public void Apply_DeleteProperty_ShouldSetTombstone()
    {
        var val = CreateJsonString("Alice");
        var doc = new Json_Document(new Dictionary<string, CrdtRegister>
        {
            { "user.name", new CrdtRegister(val, 50, "peer1") }
        });

        var op = JsonOpBatch.Create(new DeletePropertyOp("user.name", 100, "peer2"));
        var newState = _engine.Apply(doc, op);

        newState.Registers.Should().ContainKey("user.name");
        var reg = newState.Registers["user.name"];
        reg.Value.ValueKind.Should().Be(JsonValueKind.Null);
        reg.Timestamp.Should().Be(100);
        reg.PeerId.Should().Be("peer2");
    }

    [Fact]
    public void Apply_LastWriteWins_NewerTimestampWins()
    {
        var val1 = CreateJsonString("Alice");
        var doc = new Json_Document(new Dictionary<string, CrdtRegister>
        {
            { "user.name", new CrdtRegister(val1, 100, "peer1") }
        });

        var val2 = CreateJsonString("Bob");
        var op = JsonOpBatch.Create(new SetPropertyOp("user.name", val2, 105, "peer2")); // Newer

        var newState = _engine.Apply(doc, op);

        newState.Registers["user.name"].Value.GetString().Should().Be("Bob");
        newState.Registers["user.name"].PeerId.Should().Be("peer2");
    }

    [Fact]
    public void Apply_LastWriteWins_OlderTimestampIgnored()
    {
        var val1 = CreateJsonString("Alice");
        var doc = new Json_Document(new Dictionary<string, CrdtRegister>
        {
            { "user.name", new CrdtRegister(val1, 100, "peer1") }
        });

        var val2 = CreateJsonString("Bob");
        var op = JsonOpBatch.Create(new SetPropertyOp("user.name", val2, 95, "peer2")); // Older

        var newState = _engine.Apply(doc, op);

        // State remains "Alice"
        newState.Registers["user.name"].Value.GetString().Should().Be("Alice");
        newState.Registers["user.name"].PeerId.Should().Be("peer1");
    }

    [Fact]
    public void Apply_LastWriteWins_TieBreakerByPeerId()
    {
        var val1 = CreateJsonString("Alice");
        var doc = new Json_Document(new Dictionary<string, CrdtRegister>
        {
            { "user.name", new CrdtRegister(val1, 100, "peerA") }
        });

        var val2 = CreateJsonString("Bob");

        // Tie breaker: "peerB" vs "peerA". string.CompareOrdinal("peerB", "peerA") is > 0, so peerB should win.
        var op1 = JsonOpBatch.Create(new SetPropertyOp("user.name", val2, 100, "peerB"));
        var newState1 = _engine.Apply(doc, op1);

        newState1.Registers["user.name"].Value.GetString().Should().Be("Bob"); // peerB > peerA => Bob wins

        // Now test where incoming is less than existing
        var val3 = CreateJsonString("Charlie");
        var doc2 = new Json_Document(new Dictionary<string, CrdtRegister>
        {
            { "user.name", new CrdtRegister(val1, 100, "peerZ") }
        });
        var op2 = JsonOpBatch.Create(new SetPropertyOp("user.name", val3, 100, "peerA")); // peerA < peerZ

        var newState2 = _engine.Apply(doc2, op2);

        newState2.Registers["user.name"].Value.GetString().Should().Be("Alice"); // peerA < peerZ => ignored, Alice stays
    }

    [Fact]
    public void Transform_ShouldReturnIncomingUnchanged()
    {
        var opIncoming = JsonOpBatch.Create(new SetPropertyOp("a", CreateJsonString("A"), 1, "p1"));
        var opExisting = JsonOpBatch.Create(new SetPropertyOp("b", CreateJsonString("B"), 1, "p2"));

        var transformed = _engine.Transform(opIncoming, opExisting, TransformPriority.IncomingWins);

        transformed.Should().BeSameAs(opIncoming); // CRDT ops don't mutate spatially
    }

    [Fact]
    public void Invert_SetProperty_WhenDidNotExist_ShouldBecomeDeletePropertyOp()
    {
        var doc = new Json_Document();
        var op = JsonOpBatch.Create(new SetPropertyOp("user.name", CreateJsonString("Alice"), 100, "peer1"));

        var inverted = _engine.Invert(op, doc);

        inverted.Operations.Should().HaveCount(1);
        inverted.Operations[0].Should().BeOfType<DeletePropertyOp>();
        var delOp = (DeletePropertyOp)inverted.Operations[0];

        delOp.Path.Should().Be("user.name");
        delOp.PeerId.Should().Be("peer1");
        delOp.Timestamp.Should().BeGreaterThan(100); // Ensures it wins
    }

    [Fact]
    public void Invert_SetProperty_WhenExisted_ShouldRestorePreviousValue()
    {
        var valOrig = CreateJsonString("Original");
        var doc = new Json_Document(new Dictionary<string, CrdtRegister>
        {
            { "user.name", new CrdtRegister(valOrig, 50, "peer0") }
        });

        var op = JsonOpBatch.Create(new SetPropertyOp("user.name", CreateJsonString("New"), 100, "peer1"));

        var inverted = _engine.Invert(op, doc);

        inverted.Operations.Should().HaveCount(1);
        inverted.Operations[0].Should().BeOfType<SetPropertyOp>();
        var setOp = (SetPropertyOp)inverted.Operations[0];

        setOp.Path.Should().Be("user.name");
        setOp.Value.GetString().Should().Be("Original");
        setOp.PeerId.Should().Be("peer1");
        setOp.Timestamp.Should().BeGreaterThan(100);
    }

    [Fact]
    public void Invert_DeleteProperty_WhenExisted_ShouldRestorePreviousValue()
    {
        var valOrig = CreateJsonString("Original");
        var doc = new Json_Document(new Dictionary<string, CrdtRegister>
        {
            { "user.name", new CrdtRegister(valOrig, 50, "peer0") }
        });

        var op = JsonOpBatch.Create(new DeletePropertyOp("user.name", 100, "peer1"));

        var inverted = _engine.Invert(op, doc);

        inverted.Operations.Should().HaveCount(1);
        inverted.Operations[0].Should().BeOfType<SetPropertyOp>();
        var setOp = (SetPropertyOp)inverted.Operations[0];

        setOp.Path.Should().Be("user.name");
        setOp.Value.GetString().Should().Be("Original");
        setOp.PeerId.Should().Be("peer1");
        setOp.Timestamp.Should().BeGreaterThan(100);
    }

    [Fact]
    public void IsNoOp_ShouldReturnTrueForEmptyBatch()
    {
        var opEmpty = JsonOpBatch.Create();
        var opNotEmpty = JsonOpBatch.Create(new SetPropertyOp("a", CreateJsonString("A"), 1, "p1"));

        _engine.IsNoOp(opEmpty).Should().BeTrue();
        _engine.IsNoOp(opNotEmpty).Should().BeFalse();
    }
    [Fact]
    public void Invert_WithFutureTimestamp_ShouldGenerateUndoOperationThatWins()
    {
        // Bug: Invert calculates safeTimestamp based on preState's timestamp instead of the operation's timestamp.
        var valOrig = CreateJsonString("Original");
        var doc = new Json_Document(new Dictionary<string, CrdtRegister>
        {
            { "user.name", new CrdtRegister(valOrig, 50, "peer0") }
        });

        // The operation we want to undo has a timestamp far in the future
        var futureTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1000000;
        var op = JsonOpBatch.Create(new SetPropertyOp("user.name", CreateJsonString("New"), futureTimestamp, "peer1"));

        var inverted = _engine.Invert(op, doc);

        var undoOp = (SetPropertyOp)inverted.Operations[0];

        // This will FAIL because the undo timestamp will be Math.Max(UtcNow, 51), which is much smaller than futureTimestamp.
        // If we apply this undoOp, the LWW engine will ignore it!
        undoOp.Timestamp.Should().BeGreaterThan(futureTimestamp, "The undo operation must have a higher timestamp than the operation it is undoing to win the LWW resolution.");
    }

    [Fact]
    public void Compose_SequentialOperations_ShouldCombineRegisters()
    {
        // Bug: Compose always returns null.
        // This completely breaks HistoryManager's ComposeRangeAsync feature, reducing any range to just the last operation.

        var op1 = JsonOpBatch.Create(new SetPropertyOp("a", CreateJsonString("1"), 100, "peer1"));
        var op2 = JsonOpBatch.Create(new SetPropertyOp("b", CreateJsonString("2"), 105, "peer1"));

        var composed = _engine.Compose(op1, op2);

        // This will FAIL because composed is unconditionally null
        composed.Should().NotBeNull("Compose must correctly merge JsonOpBatches to support history compaction.");
        composed!.Operations.Should().HaveCount(2);
    }

    [Fact]
    public void Apply_SamePeerAndTimestamp_LaterOperationInBatchShouldWin()
    {
        // Bug: The Tie-breaker logic drops operations if Timestamp and PeerId are equal:
        // `if (opTimestamp == existingRegister.Timestamp && string.CompareOrdinal(opPeerId, existingRegister.PeerId) <= 0) continue;`
        // If a peer sends a batch with multiple changes to the same property at the same timestamp,
        // the FIRST one wins, instead of the LAST one (which represents the final intent).

        var doc = new Json_Document();

        // Peer sets it to "A", then immediately to "B" in the same batch (same timestamp)
        var op1 = new SetPropertyOp("user.status", CreateJsonString("A"), 100, "peer1");
        var op2 = new SetPropertyOp("user.status", CreateJsonString("B"), 100, "peer1");

        var batch = JsonOpBatch.Create(op1, op2);

        var newState = _engine.Apply(doc, batch);

        // This will FAIL because it will remain "A" (the second operation is dropped by the <= 0 tie-breaker)
        newState.Registers["user.status"].Value.GetString().Should().Be("B", "Sequential operations with the same timestamp from the same peer should respect batch order.");
    }

    [Fact]
    public void Apply_Commutativity_Fails_For_Same_Timestamp_And_PeerId()
    {
        var doc1 = new Json_Document();
        var doc2 = new Json_Document();

        var opA = JsonOpBatch.Create(new SetPropertyOp("prop", CreateJsonString("ValueA"), 100, "peer1"));
        var opB = JsonOpBatch.Create(new SetPropertyOp("prop", CreateJsonString("ValueB"), 100, "peer1"));

        // Node 1 receives A then B
        var state1 = _engine.Apply(doc1, opA);
        state1 = _engine.Apply(state1, opB);

        // Node 2 receives B then A
        var state2 = _engine.Apply(doc2, opB);
        state2 = _engine.Apply(state2, opA);

        // For CRDTs to guarantee Strong Eventual Consistency, the state must converge
        // to the exact same value regardless of the order of delivery.
        // Because JsonCrdtEngine.Apply overwrites when (timestamp == existing.Timestamp && peerId == existing.PeerId),
        // state1 will have "ValueB" and state2 will have "ValueA", causing divergence!
        state1.Registers["prop"].Value.GetString().Should().Be(state2.Registers["prop"].Value.GetString(), "CRDTs must commute: order of application for concurrent operations must not cause divergence.");
    }

    [Fact]
    public void Invert_IgnoredOperation_ShouldNotInflateTimestamp()
    {
        // Bug: If an operation is ignored because its timestamp is older than the current state,
        // Invert still generates an undo operation that restores the current state but with a NEW, higher timestamp.
        // Undoing an operation that had no effect should produce an empty batch (No-Op),
        // otherwise it artificially inflates the timestamp and can reject valid concurrent operations.

        var doc = new Json_Document(new Dictionary<string, CrdtRegister>
        {
            { "prop", new CrdtRegister(CreateJsonString("CurrentValue"), 1000, "peer1") }
        });

        // This operation arrives late and is older than the current state (ts 500 < 1000)
        var lateOp = JsonOpBatch.Create(new SetPropertyOp("prop", CreateJsonString("OldValue"), 500, "peer2"));

        // If we apply it, it has no effect (state remains CurrentValue at ts 1000)
        var appliedState = _engine.Apply(doc, lateOp);

        // Now we invert the late operation
        var invertedBatch = _engine.Invert(lateOp, doc);

        // The inverted batch should ideally be a No-Op, or at least applying it shouldn't change the timestamp.
        var finalState = _engine.Apply(appliedState, invertedBatch);

        // This will FAIL because Invert creates a SetPropertyOp("prop", "CurrentValue", safeTimestamp)
        // where safeTimestamp is > 1000, thus inflating the timestamp of the register unnecessarily.
        finalState.Registers["prop"].Timestamp.Should().Be(1000, "Undoing an operation that had no effect should not inflate the register's timestamp.");
    }

    [Fact]
    public void Compose_ShouldCompactOperations_ToPreventUnnecessaryUndos()
    {
        // Bug: Compose currently just concatenates lists. It does not compact.
        // If a user types "A", "AB", "ABC" on the same property, Compose yields [Set("A"), Set("AB"), Set("ABC")].
        // This is not only inefficient, but if we later try to Invert this composed batch,
        // it will generate three undo operations for the same property, potentially undoing back to "AB"
        // instead of the original pre-state if timestamps align poorly or if applied naively.

        var op1 = JsonOpBatch.Create(new SetPropertyOp("text", CreateJsonString("A"), 100, "peer1"));
        var op2 = JsonOpBatch.Create(new SetPropertyOp("text", CreateJsonString("AB"), 105, "peer1"));
        var op3 = JsonOpBatch.Create(new SetPropertyOp("text", CreateJsonString("ABC"), 110, "peer1"));

        var composed1 = _engine.Compose(op1, op2);
        var finalComposed = _engine.Compose(composed1!, op3);

        // A proper CRDT Compose must compact operations on the same path, keeping only the final LWW winner.
        // This will FAIL because finalComposed.Operations.Count == 3 instead of 1.
        finalComposed!.Operations.Should().HaveCount(1, "Compose must compact operations targeting the same property to only include the final state.");

        var op = (SetPropertyOp)finalComposed.Operations[0];
        op.Value.GetString().Should().Be("ABC");
    }

    [Fact]
    public void Apply_AlphabeticalTieBreaker_BreaksSamePeerSequentialIntent()
    {
        var doc = new Json_Document();

        // Peer sets property to "Z", then to "A" in the same batch (or same clock millisecond)
        var op1 = new SetPropertyOp("user.status", CreateJsonString("Z"), 100, "peer1");
        var op2 = new SetPropertyOp("user.status", CreateJsonString("A"), 100, "peer1");

        var batch = JsonOpBatch.Create(op1, op2);

        var newState = _engine.Apply(doc, batch);

        newState.Registers["user.status"].Value.GetString().Should().Be("A", "Sequential operations from the SAME peer at the SAME timestamp must respect their sequential order, not alphabetical value order.");
    }

    [Fact]
    public void Compose_AlphabeticalTieBreaker_PreservesSEC_Without_SequenceNumbers()
    {
        // Explanation: Without explicit sequence numbers, LWW CRDTs MUST use deterministic tie-breakers 
        // (like alphabetical order) across different batches to guarantee Strong Eventual Consistency (SEC), 
        // even if it means sacrificing chronological causal ordering for the same peer at the exact same timestamp.

        var opA = JsonOpBatch.Create(new SetPropertyOp("item", CreateJsonString("Zeta"), 100, "peer1"));
        var opB = JsonOpBatch.Create(new SetPropertyOp("item", CreateJsonString("Alpha"), 100, "peer1")); 

        var composed = _engine.Compose(opA, opB);
        var op = (SetPropertyOp)composed!.Operations[0];
        
        // Because Alpha < Zeta, Zeta MUST win to ensure SEC across nodes.
        op.Value.GetString().Should().Be("Zeta", "Deterministic tie-breaking is mathematically required to ensure convergence when timestamps collide across separate batches.");
    }

    [Fact]
    public void Compose_InconsistentWithApply_ForDifferentPeers()
    {
        var doc = new Json_Document();

        var opA = JsonOpBatch.Create(new SetPropertyOp("prop", CreateJsonString("Z"), 100, "peer1"));
        var opB = JsonOpBatch.Create(new SetPropertyOp("prop", CreateJsonString("A"), 100, "peer2"));

        // Sequential Apply
        var state1 = _engine.Apply(doc, opA);
        var stateSequential = _engine.Apply(state1, opB);

        // Composed Apply
        var composedBatch = _engine.Compose(opA, opB);
        var stateComposed = _engine.Apply(doc, composedBatch!);

        stateComposed.Registers["prop"].Value.GetString().Should().Be(
            stateSequential.Registers["prop"].Value.GetString(),
            "Compose and Apply must resolve conflicts identically."
        );
    }

    [Fact]
    public void Apply_DeleteParent_LeavesOrphanedChildren()
    {
        var doc = new Json_Document();

        var op1 = JsonOpBatch.Create(new SetPropertyOp("user.profile.name", CreateJsonString("Alice"), 100, "peer1"));
        var state1 = _engine.Apply(doc, op1);

        var op2 = JsonOpBatch.Create(new DeletePropertyOp("user.profile", 200, "peer2"));
        var state2 = _engine.Apply(state1, op2);

        // Check if any child of user.profile is still active
        var activeChildren = state2.Registers.Where(kvp => kvp.Key.StartsWith("user.profile.") && !kvp.Value.IsDeleted).ToList();

        activeChildren.Should().BeEmpty("Deleting a parent property must also tombstone or delete all its nested child properties.");
    }

    [Fact]
    public void Apply_SequentialBatches_InconsistentWithCompose_ForSamePeer()
    {
        var doc = new Json_Document();

        var opA = JsonOpBatch.Create(new SetPropertyOp("prop", CreateJsonString("Zeta"), 100, "peer1"));
        var opB = JsonOpBatch.Create(new SetPropertyOp("prop", CreateJsonString("Alpha"), 100, "peer1")); // Same peer, same TS, later in time

        // 1. Sequential Apply
        var state1 = _engine.Apply(doc, opA);
        var stateSequential = _engine.Apply(state1, opB);

        // 2. Composed Apply
        var composedBatch = _engine.Compose(opA, opB);
        var stateComposed = _engine.Apply(doc, composedBatch!);

        stateSequential.Registers["prop"].Value.GetString().Should().Be(
            stateComposed.Registers["prop"].Value.GetString(),
            "Applying batches sequentially must yield the exact same state as applying their composition."
        );
    }

    [Fact]
    public void Invert_DeleteParent_FailsToRestoreChildren()
    {
        var doc = new Json_Document();

        var op1 = JsonOpBatch.Create(
            new SetPropertyOp("user", CreateJsonString("{}"), 100, "peer1"),
            new SetPropertyOp("user.name", CreateJsonString("Alice"), 100, "peer1")
        );
        var state1 = _engine.Apply(doc, op1);

        // The deletion explicitly targets ONLY the parent "user"
        var op2 = JsonOpBatch.Create(new DeletePropertyOp("user", 200, "peer2"));
        var state2 = _engine.Apply(state1, op2);

        // Verify the child was actually deleted in state2 (the previous fix works)
        state2.Registers["user.name"].IsDeleted.Should().BeTrue();

        // Now we invert the parent deletion
        var invertedBatch = _engine.Invert(op2, state1);

        // Apply the undo
        var state3 = _engine.Apply(state2, invertedBatch);

        state3.Registers.Should().ContainKey("user.name", "Inverting a parent deletion must target the children as well.");
        state3.Registers["user.name"].IsDeleted.Should().BeFalse("The child property 'user.name' should be restored when its parent deletion is undone.");
    }

    [Fact]
    public void Apply_SetParent_LeavesOrphanedChildren()
    {
        // Bug: We fixed DeletePropertyOp leaving orphaned children, but we missed SetPropertyOp!
        // If "user.profile.name" is set, and then the parent "user.profile" is OVERWRITTEN 
        // with a completely new value (e.g., a primitive string, or a new empty object),
        // the old child "user.profile.name" remains active in the dictionary.
        // This results in corrupted JSON where a path is simultaneously a primitive and has children.

        var doc = new Json_Document();

        // 1. Set a nested child
        var op1 = JsonOpBatch.Create(new SetPropertyOp("user.profile.name", CreateJsonString("Alice"), 100, "peer1"));
        var state1 = _engine.Apply(doc, op1);

        // 2. Overwrite the parent with a primitive string
        var op2 = JsonOpBatch.Create(new SetPropertyOp("user.profile", CreateJsonString("DeletedProfile"), 200, "peer2"));
        var state2 = _engine.Apply(state1, op2);

        // Check if any child of user.profile is still active
        var activeChildren = state2.Registers.Where(kvp => kvp.Key.StartsWith("user.profile.") && !kvp.Value.IsDeleted).ToList();

        // This will FAIL because "user.profile.name" is still active and untombstoned.
        activeChildren.Should().BeEmpty("Setting a parent property to a new value must also tombstone all its previously nested child properties.");
    }

    [Fact]
    public void Compose_SetParent_LeavesOrphanedChildren()
    {
        // Bug: Similar to Apply, Compose must also clean up children when a parent is overwritten by a SetPropertyOp.
        
        var op1 = JsonOpBatch.Create(new SetPropertyOp("user.profile.name", CreateJsonString("Alice"), 100, "peer1"));
        var op2 = JsonOpBatch.Create(new SetPropertyOp("user.profile", CreateJsonString("DeletedProfile"), 200, "peer2"));

        var composed = _engine.Compose(op1, op2);

        var activeChildren = composed!.Operations.Where(op => 
        {
            string path = op switch { SetPropertyOp s => s.Path, DeletePropertyOp d => d.Path, _ => "" };
            bool isDeleted = op is DeletePropertyOp;
            return path.StartsWith("user.profile.") && !isDeleted;
        }).ToList();

        // This will FAIL because the SetPropertyOp for "user.profile.name" remains in the composed batch.
        activeChildren.Should().BeEmpty("Composing a batch that overwrites a parent must drop or tombstone operations targeting its children.");
    }
}
