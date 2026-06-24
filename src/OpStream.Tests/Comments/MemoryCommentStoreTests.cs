using FluentAssertions;
using OpStream.Server.Comments;
using System.Text.Json;
using Xunit;

namespace OpStream.Tests.Comments;

public class MemoryCommentStoreTests
{
    private static Anchor TextAnchor(int start, int end)
        => new("text", JsonSerializer.SerializeToElement(new
        {
            startOffset = start,
            endOffset = end,
            biasStart = "right",
            biasEnd = "right"
        }));

    private static Comment MakeRoot(string id, string docId, int anchoredAt = 0)
        => new(
            Id: id,
            DocumentId: docId,
            ParentCommentId: null,
            AuthorPeerId: "peer-1",
            AuthorName: "Test User",
            Body: "hello",
            Anchor: TextAnchor(0, 5),
            AnchoredAtRevision: anchoredAt,
            CreatedAt: DateTimeOffset.UtcNow,
            ResolvedAt: null,
            ResolvedByPeerId: null,
            IsOrphaned: false);

    [Fact]
    public async Task Add_then_LoadOpen_returns_the_comment()
    {
        var store = new MemoryCommentStore();
        var c = MakeRoot("c1", "doc-1");

        await store.AddAsync(c);

        var open = await store.LoadOpenAsync("doc-1");
        open.Should().ContainSingle(x => x.Id == "c1");
    }

    [Fact]
    public async Task Delete_root_cascades_to_replies()
    {
        var store = new MemoryCommentStore();
        var root = MakeRoot("root", "doc-1");
        var reply = root with { Id = "reply-1", ParentCommentId = "root", Anchor = null };

        await store.AddAsync(root);
        await store.AddAsync(reply);

        await store.DeleteAsync("root");

        var open = await store.LoadOpenAsync("doc-1");
        open.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateAnchors_bumps_AnchoredAtRevision_and_flags_orphans()
    {
        var store = new MemoryCommentStore();
        var c = MakeRoot("c1", "doc-1", anchoredAt: 0);
        await store.AddAsync(c);

        var newAnchor = TextAnchor(10, 15);
        await store.UpdateAnchorsAsync("doc-1", revision: 7, new[]
        {
            new AnchorUpdate("c1", newAnchor, AnchorOutcome.Orphaned)
        });

        var fetched = await store.GetAsync("c1");
        fetched!.AnchoredAtRevision.Should().Be(7);
        fetched.IsOrphaned.Should().BeTrue();
    }

    [Fact]
    public async Task GetMinAnchoredRevision_ignores_resolved_and_replies()
    {
        var store = new MemoryCommentStore();
        await store.AddAsync(MakeRoot("a", "doc-1", anchoredAt: 5));
        await store.AddAsync(MakeRoot("b", "doc-1", anchoredAt: 12));
        var resolved = MakeRoot("c", "doc-1", anchoredAt: 1) with
        {
            ResolvedAt = DateTimeOffset.UtcNow,
            ResolvedByPeerId = "peer-2"
        };
        await store.AddAsync(resolved);

        var min = await store.GetMinAnchoredRevisionAsync("doc-1");
        min.Should().Be(5);
    }
}
