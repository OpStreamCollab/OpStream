using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpStream.Constants;
using OpStream.Server.Comments;
using OpStream.Server.Engine;
using OpStream.Server.Engine.Text;
using OpStream.Server.Session;
using OpStream.Server.Snapshots;
using OpStream.Server.Storage;
using OpStream.Shared.Abstractions;
using OpStream.Shared.Messages;
using System.Text.Json;
using Xunit;

namespace OpStream.Tests.Comments;

/// <summary>
/// End-to-end smoke test: a TextOp applied via DocumentSession runs CommentAnchorRebaseHook,
/// which feeds anchor updates back into the ICommentStore and into OpAppliedBackplanePayload.
/// </summary>
public class CommentAnchorRebaseHookTests
{
    private static Anchor TextAnchor(int start, int end)
        => new("text", JsonSerializer.SerializeToElement(new
        {
            startOffset = start,
            endOffset = end,
            biasStart = "right",
            biasEnd = "right"
        }));

    [Fact]
    public async Task ApplyOp_rebases_anchor_and_persists_update()
    {
        const string docId = "doc-1";
        var commentStore = new MemoryCommentStore();
        await commentStore.AddAsync(new Comment(
            Id: "c1",
            DocumentId: docId,
            ParentCommentId: null,
            AuthorPeerId: "peer-A",
            Body: "look here",
            Anchor: TextAnchor(0, 5),
            AnchoredAtRevision: 0,
            CreatedAt: DateTimeOffset.UtcNow,
            ResolvedAt: null,
            ResolvedByPeerId: null,
            IsOrphaned: false));

        var hook = new CommentAnchorRebaseHook<TextOp>(
            new IAnchorEngine<TextOp>[] { new TextAnchorEngine() },
            commentStore,
            NullLogger<CommentAnchorRebaseHook<TextOp>>.Instance);

        var storeMock = new Mock<IDocumentStore>();
        storeMock.Setup(s => s.StreamOpsAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                 .Returns(AsyncEnumerable.Empty<StoredOp>());

        var backplaneMock = new Mock<IBackplane>();
        backplaneMock.Setup(b => b.NodeId).Returns("node-1");
        BackplaneMessage? captured = null;
        backplaneMock
            .Setup(b => b.PublishAsync(It.IsAny<string>(), It.IsAny<BackplaneMessage>(), It.IsAny<CancellationToken>()))
            .Callback<string, BackplaneMessage, CancellationToken>((_, m, _) => captured = m)
            .Returns(Task.CompletedTask);

        var snapshotter = new Mock<IOpSnapshotter>();
        var historySnapshotter = new Mock<IOpHistorySnapshotter>();

        var session = new DocumentSession<TextDocument, TextOp>(
            docId,
            new TextDocument("Hello world"),
            new TextOtEngine(),
            initialRevision: 0,
            storeMock.Object,
            backplaneMock.Object,
            snapshotter.Object,
            historySnapshotter.Object,
            Array.Empty<IOpValidator<TextOp>>(),
            NullLogger<DocumentSession<TextDocument, TextOp>>.Instance,
            new IPostApplyHook<TextOp>[] { hook });

        // Insert 3 chars at offset 0 — anchor [0,5] should shift to [3,8] under right-bias.
        var op = new TextOp(new TextOpComponent[] { new Insert("Oh ") });
        var payload = JsonSerializer.SerializeToUtf8Bytes(op, OpStreamJsonOptions.Default);

        var result = await session.ApplyOpAsync("peer-B", payload, baseRevision: 0);
        result.Success.Should().BeTrue();

        // Persisted anchor moved.
        var updated = await commentStore.GetAsync("c1");
        updated.Should().NotBeNull();
        updated!.AnchoredAtRevision.Should().Be(1);
        updated.Anchor!.Data.GetProperty("startOffset").GetInt32().Should().Be(3);
        updated.Anchor.Data.GetProperty("endOffset").GetInt32().Should().Be(8);

        // Broadcast carried the anchor update too.
        captured.Should().NotBeNull();
        var broadcast = JsonSerializer.Deserialize<OpAppliedBackplanePayload>(captured!.Payload.Span, OpStreamJsonOptions.Default);
        broadcast!.AnchorUpdates.Should().NotBeNull();
        broadcast.AnchorUpdates!.Should().ContainSingle(u => u.CommentId == "c1" && u.Outcome == AnchorOutcome.Moved);
    }
}

internal static class AsyncEnumerable
{
    public static async IAsyncEnumerable<T> Empty<T>()
    {
        await Task.CompletedTask;
        yield break;
    }
}
