using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpStream.Constants;
using OpStream.Server.Comments;
using OpStream.Server.Engine.Text;
using OpStream.Server.Models;
using OpStream.Server.Storage;
using System.Text.Json;
using Xunit;

namespace OpStream.Tests.Comments;

public class CompactWithAnchorsServiceTests
{
    private static Anchor TextAnchor(int start, int end)
        => new("text", JsonSerializer.SerializeToElement(new
        {
            startOffset = start, endOffset = end, biasStart = "right", biasEnd = "right"
        }));

    [Fact]
    public async Task No_open_root_comments_calls_compact_directly()
    {
        var store = new MemoryCommentStore();
        var docStore = new Mock<IDocumentStore>();
        var registry = new Mock<IAnchorEngineRegistry>();
        var svc = new CompactWithAnchorsService(
            store, docStore.Object, registry.Object,
            NullLogger<CompactWithAnchorsService>.Instance);

        await svc.CompactAsync("doc1", upToRevision: 10);

        docStore.Verify(d => d.CompactAsync("doc1", 10, It.IsAny<CancellationToken>()), Times.Once);
        registry.Verify(r => r.TryGet(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Open_comment_is_rebased_before_compact()
    {
        const string docId = "doc-rebase";
        var commentStore = new MemoryCommentStore();
        await commentStore.AddAsync(new Comment(
            Id: "c1", DocumentId: docId, ParentCommentId: null,
            AuthorPeerId: "p1", Body: "hello",
            Anchor: TextAnchor(0, 5), AnchoredAtRevision: 0,
            CreatedAt: DateTimeOffset.UtcNow,
            ResolvedAt: null, ResolvedByPeerId: null, IsOrphaned: false));

        var textEngine = new TextAnchorEngine();
        var adapter = new AnchorEngineAdapter<TextOp>(textEngine);
        var registry = new AnchorEngineRegistry(
            new[] { new AnchorEngineRegistration("text", adapter) });

        // Insert "Oh " → anchor [0,5] should move to [3,8]
        var op = new TextOp(new TextOpComponent[] { new Insert("Oh ") });
        var opBytes = JsonSerializer.SerializeToUtf8Bytes(op, OpStreamJsonOptions.Default);
        var stored = new StoredOp(1, "p1", DateTimeOffset.UtcNow, opBytes, "text");

        var docStoreMock = new Mock<IDocumentStore>();
        docStoreMock
            .Setup(d => d.StreamOpsAsync(docId, It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns(YieldOps(stored));

        var svc = new CompactWithAnchorsService(
            commentStore, docStoreMock.Object, registry,
            NullLogger<CompactWithAnchorsService>.Instance);

        await svc.CompactAsync(docId, upToRevision: 5);

        // Anchor should be updated.
        var updated = await commentStore.GetAsync("c1");
        updated!.Anchor!.Data.GetProperty("startOffset").GetInt32().Should().Be(3);
        updated.Anchor.Data.GetProperty("endOffset").GetInt32().Should().Be(8);

        // Compact was called.
        docStoreMock.Verify(d => d.CompactAsync(docId, 5, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static async IAsyncEnumerable<StoredOp> YieldOps(params StoredOp[] ops)
    {
        foreach (var op in ops)
        {
            yield return op;
            await Task.CompletedTask;
        }
    }
}
