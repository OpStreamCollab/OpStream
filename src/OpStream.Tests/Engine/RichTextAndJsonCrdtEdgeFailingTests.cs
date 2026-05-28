using FluentAssertions;
using OpStream.Server.Engine.Json;
using OpStream.Server.Engine.RichText;
using OpStream.Server.Models;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace OpStream.Tests.Engine
{
    /// <summary>
    /// Edge tests intentionally designed to expose latent bugs in
    /// <see cref="RichTextEngine"/> and <see cref="JsonCrdtEngine"/>.
    /// Every test in this file is expected to FAIL against the current implementation.
    /// </summary>
    public class RichTextAndJsonCrdtEdgeFailingTests
    {
        // ------------------------------------------------------------
        // RichTextEngine
        // ------------------------------------------------------------

        // BUG: AttributesEqual compares values via .ToString(). The integer 1 and the
        // string "1" therefore compare equal. We observe this through Apply: two
        // adjacent inserts with semantically different typed values get merged into
        // one by DocumentBuilder, silently losing the type distinction.
        [Fact]
        public void RichText_Apply_AdjacentInsertsWithDifferentTypedAttributes_ShouldNotBeMerged()
        {
            var engine = new RichTextEngine();
            var doc = new RichTextDocument();

            var op = RichTextOp.Create(
                new Insert("A", new TextAttributes { ["size"] = 1 }),
                new Insert("B", new TextAttributes { ["size"] = "1" })
            );

            var state = engine.Apply(doc, op);

            state.Content.Should().HaveCount(2,
                "adjacent inserts with attributes that differ by type must remain separate runs");
        }

        // BUG: IsNoOp only recognises pure Retains. An operation made exclusively of
        // Insert("") components has no effect on the document but IsNoOp returns false,
        // so the host stores and broadcasts an empty change instead of dropping it.
        [Fact]
        public void RichText_IsNoOp_WithEmptyInsert_ShouldBeTrue()
        {
            var engine = new RichTextEngine();
            var op = RichTextOp.Create(new Insert(""));

            engine.IsNoOp(op).Should().BeTrue("inserting empty text has no observable effect");
        }

        // BUG: IsNoOp does not recognise Delete(0) as a no-op either.
        [Fact]
        public void RichText_IsNoOp_WithZeroLengthDelete_ShouldBeTrue()
        {
            var engine = new RichTextEngine();
            var op = RichTextOp.Create(new Delete(0));

            engine.IsNoOp(op).Should().BeTrue("deleting zero characters has no observable effect");
        }

        // BUG: Inverting Insert("") produces Delete(0) which is not a no-op. The
        // history layer therefore stores meaningless undo entries.
        [Fact]
        public void RichText_Invert_OfEmptyInsert_ShouldBeNoOp()
        {
            var engine = new RichTextEngine();
            var doc = new RichTextDocument();
            var op = RichTextOp.Create(new Insert(""));

            var inverted = engine.Invert(op, doc);

            engine.IsNoOp(inverted)
                .Should().BeTrue("the inverse of a no-op insert must itself be a no-op");
        }

        // BUG: Compose enters the "two sides not Insert" branch and executes
        // `if (length <= 0) break;` whenever a zero-length op heads either iterator,
        // dropping the rest of the other operation silently.
        [Fact]
        public void RichText_Compose_EmptyInsertHeadInA_ShouldNotDropOpB()
        {
            var engine = new RichTextEngine();
            var doc = new RichTextDocument(new[] { new Insert("ABC") });

            var opA = RichTextOp.Create(new Insert(""));
            var opB = RichTextOp.Create(new Retain(3), new Insert("X"));

            var sequential = engine.Apply(engine.Apply(doc, opA), opB);
            var composed = engine.Compose(opA, opB);

            composed.Should().NotBeNull("composing valid operations must not collapse to null");
            var composedState = engine.Apply(doc, composed!);

            string.Concat(composedState.Content.Select(i => i.Text))
                .Should().Be(string.Concat(sequential.Content.Select(i => i.Text)),
                    "Compose must yield the same final state as sequential Apply");
        }

        // BUG: Same problem reachable through a zero-length Retain at the head of opA.
        [Fact]
        public void RichText_Compose_ZeroRetainHeadInA_ShouldNotDropOpB()
        {
            var engine = new RichTextEngine();
            var doc = new RichTextDocument(new[] { new Insert("ABC") });

            var opA = RichTextOp.Create(new Retain(0));
            var opB = RichTextOp.Create(new Retain(3), new Insert("X"));

            var sequential = engine.Apply(engine.Apply(doc, opA), opB);
            var composed = engine.Compose(opA, opB);

            composed.Should().NotBeNull("composing valid operations must not collapse to null");
            var composedState = engine.Apply(doc, composed!);

            string.Concat(composedState.Content.Select(i => i.Text))
                .Should().Be(string.Concat(sequential.Content.Select(i => i.Text)));
        }

        // BUG: Transform simply concatenates transformed chunks into the result list
        // but never calls Compact(). This leaves adjacent identical operations fragmented,
        // which violates the standard OT normalization rules.
        [Fact]
        public void RichText_Transform_ShouldCompactResultingOperations()
        {
            var engine = new RichTextEngine();
            
            var opAlice = RichTextOp.Create(new Insert("A"), new Insert("B"));
            var opBob = RichTextOp.Create(new Retain(0));

            var transformed = engine.Transform(opAlice, opBob, TransformPriority.IncomingWins);

            transformed.Should().NotBeNull();
            transformed!.Components.Should().HaveCount(1, 
                "Transform should compact adjacent Insert components into a single Insert");
        }

        // BUG: Similar to above, adjacent Retains with identical attributes remain fragmented
        // after Transform because it lacks a final Compact() pass.
        [Fact]
        public void RichText_Transform_ShouldCompactAdjacentRetains()
        {
            var engine = new RichTextEngine();
            
            var opAlice = RichTextOp.Create(
                new Retain(2, new TextAttributes { ["bold"] = true }),
                new Retain(2, new TextAttributes { ["bold"] = true })
            );
            var opBob = RichTextOp.Create(new Retain(4));

            var transformed = engine.Transform(opAlice, opBob, TransformPriority.IncomingWins);

            transformed.Should().NotBeNull();
            transformed!.Components.Should().HaveCount(1, 
                "Transform should merge adjacent Retains with identical attributes");
        }

        // ------------------------------------------------------------
        // JsonCrdtEngine
        // ------------------------------------------------------------

        private static readonly JsonCrdtEngine Json = new();

        private static JsonElement El(string raw) { using var d = JsonDocument.Parse(raw); return d.RootElement.Clone(); }
        private static JsonElement S(string s) => El($"\"{s}\"");

        // BUG: When a single batch contains a parent Set after a child Set with the
        // same timestamp and peer (sequential user intent), Compose drops the child
        // because the same-ts-same-peer LWW collapse fires during compaction. The
        // pathsInBatch protection only exists in Apply, not in Compose, and Apply runs
        // Compose first, so the child never reaches Apply.
        [Fact]
        public void Json_Apply_ParentSetWithExplicitChildInSameBatch_ShouldPreserveChild()
        {
            var doc = new Json_Document();

            var batch = JsonOpBatch.Create(
                new SetPropertyOp("user.name", S("Alice"), 200, "p1"),
                new SetPropertyOp("user", S("X"), 200, "p1")
            );

            var newState = Json.Apply(doc, batch);

            newState.Registers.Should().ContainKey("user.name");
            newState.Registers["user.name"].IsDeleted.Should().BeFalse(
                "an explicit child Set in the same batch as a parent Set must not be tombstoned");
            newState.Registers["user.name"].Value.GetString().Should().Be("Alice");
        }

        // BUG: When a previously deleted parent is restored via Invert, the engine
        // tombstones siblings that were legitimately added by other peers between the
        // delete and the undo. Undo of a delete must not stomp on concurrent writes.
        [Fact]
        public void Json_Apply_RestoreDeletedParent_ShouldNotTombstoneNewlyAddedChildren()
        {
            var doc = new Json_Document();

            var state1 = Json.Apply(doc, JsonOpBatch.Create(
                new SetPropertyOp("user", S("X"), 100, "p1")));

            var deleteParent = JsonOpBatch.Create(new DeletePropertyOp("user", 200, "p1"));
            var state2 = Json.Apply(state1, deleteParent);

            var state3 = Json.Apply(state2, JsonOpBatch.Create(
                new SetPropertyOp("user.name", S("Charlie"), 300, "p2")));

            var inverse = Json.Invert(deleteParent, state1);

            var restored = Json.Apply(state3, inverse);

            restored.Registers.Should().ContainKey("user.name");
            restored.Registers["user.name"].IsDeleted.Should().BeFalse(
                "undoing a parent deletion must not tombstone siblings added by other peers afterwards");
            restored.Registers["user.name"].Value.GetString().Should().Be("Charlie");
        }

        // BUG: RestampToWin uses `Math.Max(maxExisting, UtcNow) + 1`. It does NOT
        // consider timestamps inside `op.Operations` itself. If the inverse batch
        // already carries timestamps higher than maxExisting (e.g. produced from a
        // previous Invert that pushed ts far into the future), RestampToWin demotes
        // them, so the inverse loses against the very value it is trying to undo.
        [Fact]
        public void Json_RestampToWin_ShouldNotDemoteOperationsAboveExisting()
        {
            var doc = new Json_Document(new System.Collections.Generic.Dictionary<string, CrdtRegister>
            {
                { "prop", new CrdtRegister(S("current"), 1000, "p1") }
            });

            var farFuture = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10_000_000L;
            var op = JsonOpBatch.Create(new SetPropertyOp("prop", S("undo"), farFuture, "p1"));

            var restamped = Json.RestampToWin(op, doc);
            var ts = ((SetPropertyOp)restamped.Operations[0]).Timestamp;

            ts.Should().BeGreaterThanOrEqualTo(farFuture,
                "RestampToWin must never produce a timestamp lower than the operation already carried");
        }

        // BUG: ApplySingleOp only checks if the EXACT path exists in `registers`.
        // It never checks if an ANCESTOR was deleted with a newer timestamp.
        // Therefore, a stale SetPropertyOp on a child will successfully resurrect
        // the child even if the parent has a newer tombstone!
        [Fact]
        public void Json_Apply_StaleChildSet_ShouldNotResurrectIfParentHasNewerTombstone()
        {
            var doc = new Json_Document();
            
            var batch1 = JsonOpBatch.Create(new DeletePropertyOp("user", 200, "p1"));
            var state1 = Json.Apply(doc, batch1);

            // A delayed operation for a child path arrives with an older timestamp (100 < 200)
            var batch2 = JsonOpBatch.Create(new SetPropertyOp("user.name", S("Alice"), 100, "p2"));
            var state2 = Json.Apply(state1, batch2);

            state2.Registers.Should().NotContainKey("user.name", 
                "a stale child operation should not be applied if its parent has a newer tombstone");
        }

        // BUG: Similar to above, if the parent was overwritten with a new value 
        // at a newer timestamp, a stale child operation should be ignored.
        [Fact]
        public void Json_Apply_StaleChildSet_ShouldNotResurrectIfParentHasNewerSet()
        {
            var doc = new Json_Document();
            
            var batch1 = JsonOpBatch.Create(new SetPropertyOp("user", S("{}"), 200, "p1"));
            var state1 = Json.Apply(doc, batch1);

            // Stale child arrives (100 < 200)
            var batch2 = JsonOpBatch.Create(new SetPropertyOp("user.name", S("Alice"), 100, "p2"));
            var state2 = Json.Apply(state1, batch2);

            state2.Registers.Should().NotContainKey("user.name", 
                "a stale child operation should not be applied if its parent was overwritten at a later timestamp");
        }

        // BUG: Compose processes operations sequentially and its `childrenPaths` cleanup
        // only removes children that are ALREADY in the `compacted` dictionary.
        // If a parent Delete comes BEFORE a stale child Set in the sequence of batches being composed,
        // the child Set is simply appended and survives the composition!
        [Fact]
        public void Json_Compose_StaleChildSetAfterParentTombstone_ShouldDropChild()
        {
            var batch1 = JsonOpBatch.Create(new DeletePropertyOp("user", 200, "p1"));
            var batch2 = JsonOpBatch.Create(new SetPropertyOp("user.name", S("Alice"), 100, "p2"));

            var composed = Json.Compose(batch1, batch2);

            composed!.Operations.Should().HaveCount(1, 
                "composing a stale child operation after a newer parent tombstone should drop the child operation");
            composed.Operations[0].Should().BeOfType<DeletePropertyOp>();
        }
    }
}
