using FluentAssertions;
using OpStream.Server.Comments;
using OpStream.Server.Engine.Json;
using System.Text.Json;
using Xunit;

namespace OpStream.Tests.Comments;

public class JsonPathAnchorEngineTests
{
    private readonly JsonPathAnchorEngine _engine = new();

    private static Anchor Make(string path)
    {
        var data = JsonSerializer.SerializeToElement(new { path });
        return new Anchor("json", data);
    }

    [Fact]
    public void Delete_exact_path_orphans_anchor()
    {
        var anchor = Make("root.users[3].name");
        var batch = JsonOpBatch.Create(new DeletePropertyOp("root.users[3].name", 1, "peer1"));

        var result = _engine.Rebase(anchor, batch);

        result.Outcome.Should().Be(AnchorOutcome.Orphaned);
    }

    [Fact]
    public void Delete_ancestor_path_orphans_anchor()
    {
        var anchor = Make("root.users[3].name");
        var batch = JsonOpBatch.Create(new DeletePropertyOp("root.users[3]", 1, "peer1"));

        var result = _engine.Rebase(anchor, batch);

        result.Outcome.Should().Be(AnchorOutcome.Orphaned);
    }

    [Fact]
    public void Delete_unrelated_path_leaves_anchor_unchanged()
    {
        var anchor = Make("root.title");
        var batch = JsonOpBatch.Create(new DeletePropertyOp("root.users[3].name", 1, "peer1"));

        var result = _engine.Rebase(anchor, batch);

        result.Outcome.Should().Be(AnchorOutcome.Unchanged);
    }

    [Fact]
    public void Set_op_never_orphans_anchor()
    {
        var anchor = Make("root.title");
        var batch = JsonOpBatch.Create(
            new SetPropertyOp("root.title", JsonSerializer.SerializeToElement("new title"), 1, "peer1"));

        var result = _engine.Rebase(anchor, batch);

        result.Outcome.Should().Be(AnchorOutcome.Unchanged);
    }

    [Fact]
    public void Non_json_kind_passes_through_unchanged()
    {
        var data = JsonSerializer.SerializeToElement(new { path = "root.x" });
        var anchor = new Anchor("text", data);
        var batch = JsonOpBatch.Create(new DeletePropertyOp("root.x", 1, "peer1"));

        var result = _engine.Rebase(anchor, batch);

        result.Outcome.Should().Be(AnchorOutcome.Unchanged);
        result.Anchor.Should().Be(anchor);
    }
}
