using FluentAssertions;
using OpStream.Server.Comments;
using OpStream.Server.Engine.RichText;
using System.Text.Json;
using Xunit;
using RichTextInsert = OpStream.Server.Engine.RichText.Insert;
using RichTextRetain = OpStream.Server.Engine.RichText.Retain;
using RichTextDelete = OpStream.Server.Engine.RichText.Delete;

namespace OpStream.Tests.Comments;

public class RichTextAnchorEngineTests
{
    private readonly RichTextAnchorEngine _engine = new();

    private static Anchor Make(int start, int end, string biasStart = "right", string biasEnd = "right")
    {
        var data = JsonSerializer.SerializeToElement(new { startOffset = start, endOffset = end, biasStart, biasEnd });
        return new Anchor("richtext", data);
    }

    private static (int Start, int End) ReadOffsets(Anchor a)
        => (a.Data.GetProperty("startOffset").GetInt32(), a.Data.GetProperty("endOffset").GetInt32());

    [Fact]
    public void Insert_before_anchor_shifts_offsets()
    {
        var anchor = Make(5, 10);
        var op = RichTextOp.Create(new RichTextInsert("Hi "));

        var result = _engine.Rebase(anchor, op);

        result.Outcome.Should().Be(AnchorOutcome.Moved);
        ReadOffsets(result.Anchor).Should().Be((8, 13));
    }

    [Fact]
    public void Delete_covering_anchor_orphans_it()
    {
        var anchor = Make(2, 7);
        var op = RichTextOp.Create(new RichTextDelete(10));

        var result = _engine.Rebase(anchor, op);

        result.Outcome.Should().Be(AnchorOutcome.Orphaned);
    }

    [Fact]
    public void Retain_does_not_move_anchor()
    {
        var anchor = Make(3, 8);
        var op = RichTextOp.Create(new RichTextRetain(20));

        var result = _engine.Rebase(anchor, op);

        result.Outcome.Should().Be(AnchorOutcome.Unchanged);
    }

    [Fact]
    public void Non_richtext_kind_passes_through_unchanged()
    {
        var data = JsonSerializer.SerializeToElement(new { startOffset = 0, endOffset = 5 });
        var anchor = new Anchor("text", data);
        var op = RichTextOp.Create(new RichTextInsert("X"));

        var result = _engine.Rebase(anchor, op);

        result.Outcome.Should().Be(AnchorOutcome.Unchanged);
        result.Anchor.Should().Be(anchor);
    }
}
