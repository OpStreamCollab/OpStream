using FluentAssertions;
using OpStream.Server.Engine.Form;
using System.Text.Json;
using Xunit;

namespace OpStream.Tests.Engine;

/// <summary>
/// Edge-case tests for <see cref="FormOtEngine"/>.
/// </summary>
public class FormOtEngineTests
{
    private readonly FormOtEngine _engine = new();

    private static JsonElement Str(string s)
    {
        using var doc = JsonDocument.Parse($"\"{s}\"");
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Apply_BasicSet_WritesField()
    {
        var batch = FormOpBatch.Create(new SetFieldOp("email", Str("a@b"), 1, "p"));
        var state = _engine.Apply(new FormDocument(), batch);
        state.Fields["email"].Value.GetString().Should().Be("a@b");
    }

    /// <summary>
    /// Convergence under same timestamp must be deterministic across replicas
    /// — fallback ordering is peerId ordinal.
    /// </summary>
    [Fact]
    public void Apply_SameTimestampDifferentPeers_BreaksTieByPeerId()
    {
        var s1 = _engine.Apply(new FormDocument(),
            FormOpBatch.Create(new SetFieldOp("f", Str("aaa-val"), 100, "aaa")));
        var s2 = _engine.Apply(s1,
            FormOpBatch.Create(new SetFieldOp("f", Str("zzz-val"), 100, "zzz")));
        s2.Fields["f"].Value.GetString().Should().Be("zzz-val");
    }

    /// <summary>
    /// Round-trip Invert ∘ Apply on the SAME field must restore the pre-state for that field.
    /// </summary>
    [Fact]
    public void Invert_ThenApply_RestoresPreStateForTouchedField()
    {
        var pre = _engine.Apply(new FormDocument(),
            FormOpBatch.Create(new SetFieldOp("name", Str("old"), 1, "p")));

        var op = FormOpBatch.Create(new SetFieldOp("name", Str("new"), 10, "p"));
        var post = _engine.Apply(pre, op);
        post.Fields["name"].Value.GetString().Should().Be("new");

        var inverse = _engine.Invert(op, pre);
        var restored = _engine.Apply(post, inverse);

        restored.Fields["name"].Value.GetString().Should().Be("old",
            "Invert+Apply must restore the field's prior value");
    }

    /// <summary>
    /// Invert of a SetField on a previously-unset field returns a ClearFieldOp. After applying
    /// that inverse, the field MUST be observable as cleared (null register) — NOT absent,
    /// because we can never truly delete a register without breaking LWW convergence. This
    /// asymmetry between "never set" and "explicitly cleared" is intentional but worth a guard.
    /// </summary>
    [Fact]
    public void Invert_SetOnUnsetField_ProducesClearOpThatLeavesNullRegister()
    {
        var pre = new FormDocument();
        var op = FormOpBatch.Create(new SetFieldOp("optional", Str("foo"), 5, "p"));
        var post = _engine.Apply(pre, op);

        var inverse = _engine.Invert(op, pre);
        var restored = _engine.Apply(post, inverse);

        restored.Fields.Should().ContainKey("optional", "the field stays as a tombstoned null register, not absent");
        restored.Fields["optional"].Value.ValueKind.Should().Be(JsonValueKind.Null);
    }

    /// <summary>
    /// <c>RestampToWin</c> must produce timestamps strictly above the current max.
    /// </summary>
    [Fact]
    public void RestampToWin_ProducedOpsBeatEveryExistingTimestamp()
    {
        long farFutureTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1_000_000;
        var state = _engine.Apply(new FormDocument(),
            FormOpBatch.Create(new SetFieldOp("f", Str("future"), farFutureTs, "p")));

        var ancientInverse = FormOpBatch.Create(new SetFieldOp("f", Str("undo"), 1, "p"));
        var restamped = _engine.RestampToWin(ancientInverse, state);

        var after = _engine.Apply(state, restamped);
        after.Fields["f"].Value.GetString().Should().Be("undo");
    }
}
