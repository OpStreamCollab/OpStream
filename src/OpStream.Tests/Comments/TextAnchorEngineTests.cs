using FluentAssertions;
using OpStream.Server.Comments;
using OpStream.Server.Engine.Text;
using System.Text.Json;
using Xunit;

namespace OpStream.Tests.Comments;

public class TextAnchorEngineTests
{
    private readonly TextAnchorEngine _engine = new();

    private static Anchor Make(int start, int end, string biasStart = "right", string biasEnd = "right")
    {
        var data = JsonSerializer.SerializeToElement(new
        {
            startOffset = start,
            endOffset = end,
            biasStart,
            biasEnd
        });
        return new Anchor("text", data);
    }

    private static (int Start, int End) ReadOffsets(Anchor a)
        => (a.Data.GetProperty("startOffset").GetInt32(),
            a.Data.GetProperty("endOffset").GetInt32());

    [Fact]
    public void Insert_before_anchor_shifts_both_offsets_right()
    {
        // Doc: "Hello world", anchor on "world" → [6,11]
        var anchor = Make(6, 11);
        var op = new TextOp(new TextOpComponent[] { new Insert("Oh ") });

        var result = _engine.Rebase(anchor, op);

        result.Outcome.Should().Be(AnchorOutcome.Moved);
        ReadOffsets(result.Anchor).Should().Be((9, 14));
    }

    [Fact]
    public void Insert_after_anchor_does_not_move_it()
    {
        var anchor = Make(0, 5);
        var op = new TextOp(new TextOpComponent[] { new Retain(10), new Insert("!") });

        var result = _engine.Rebase(anchor, op);

        result.Outcome.Should().Be(AnchorOutcome.Unchanged);
    }

    [Fact]
    public void Delete_entirely_inside_anchor_shrinks_it()
    {
        // Anchor [0,10], delete 3 chars at offset 4 → [0,7]
        var anchor = Make(0, 10);
        var op = new TextOp(new TextOpComponent[] { new Retain(4), new Delete(3) });

        var result = _engine.Rebase(anchor, op);

        result.Outcome.Should().Be(AnchorOutcome.Moved);
        ReadOffsets(result.Anchor).Should().Be((0, 7));
    }

    [Fact]
    public void Delete_covering_anchor_marks_orphaned()
    {
        // Anchor [3,7], delete [0..20]
        var anchor = Make(3, 7);
        var op = new TextOp(new TextOpComponent[] { new Delete(20) });

        var result = _engine.Rebase(anchor, op);

        result.Outcome.Should().Be(AnchorOutcome.Orphaned);
    }

    [Fact]
    public void Insert_at_anchor_start_with_left_bias_keeps_offset()
    {
        var anchor = Make(5, 10, biasStart: "left");
        var op = new TextOp(new TextOpComponent[] { new Retain(5), new Insert("xx") });

        var result = _engine.Rebase(anchor, op);

        result.Outcome.Should().Be(AnchorOutcome.Moved);
        var (start, end) = ReadOffsets(result.Anchor);
        start.Should().Be(5);          // left bias keeps it
        end.Should().Be(12);           // right bias on end pushes it
    }

    [Fact]
    public void Non_text_kind_is_passthrough()
    {
        var jsonAnchor = new Anchor("json", JsonSerializer.SerializeToElement(new { path = "a.b" }));
        var op = new TextOp(new TextOpComponent[] { new Insert("xxx") });

        var result = _engine.Rebase(jsonAnchor, op);

        result.Outcome.Should().Be(AnchorOutcome.Unchanged);
        result.Anchor.Should().BeSameAs(jsonAnchor);
    }
}
