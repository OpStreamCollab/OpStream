using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OpStream.Constants;
using OpStream.Server.Engine.Text;
using OpStream.Server.Session;
using OpStream.Server.Storage;
using Xunit;

namespace OpStream.Tests.Session;

/// <summary>
/// Integration tests for the "document drain" feature: when the last peer leaves a document,
/// registered <see cref="IDocumentDrainHandler"/> implementations are invoked with the final
/// state, and may instruct OpStream to delete the document entirely.
/// </summary>
public class DocumentDrainTests
{
    /// <summary>Shared sink so a scoped handler can record what it observed.</summary>
    private sealed class DrainRecorder
    {
        public int Calls;
        public DocumentDrainContext? Last;
        public DocumentDrainDecision Decision = DocumentDrainDecision.Keep;
    }

    private sealed class RecordingDrainHandler(DrainRecorder recorder) : IDocumentDrainHandler
    {
        public ValueTask<DocumentDrainDecision> OnDocumentDrainedAsync(
            DocumentDrainContext ctx, CancellationToken ct = default)
        {
            recorder.Calls++;
            recorder.Last = ctx;
            return ValueTask.FromResult(recorder.Decision);
        }
    }

    private static (DocumentRouter router, IDocumentStore store) BuildRouter(DrainRecorder recorder)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(recorder);
        services.AddOpStream()
            .AddDocumentDrainHandler<RecordingDrainHandler>();

        var sp = services.BuildServiceProvider();
        var router = sp.GetRequiredService<DocumentRouter>();
        router.InitializeAsync().GetAwaiter().GetResult();
        return (router, sp.GetRequiredService<IDocumentStore>());
    }

    private static async Task<long> JoinAndEditAsync(DocumentRouter router, string docId, string peerId)
    {
        var join = await router.JoinDocumentAsync(docId, "text", peerId, ProtocolVersions.Current);
        join.Success.Should().BeTrue();

        var op = new TextOp(new TextOpComponent[] { new Insert("Hello") });
        var payload = JsonSerializer.SerializeToUtf8Bytes(op, OpStreamJsonOptions.Default);
        var applied = await router.ApplyOpAsync(peerId, docId, payload, baseRevision: join.Value!.Revision);
        applied.Success.Should().BeTrue();
        return applied.Value!.NewRevision;
    }

    [Fact]
    public async Task LastPeerLeaving_InvokesDrainHandler_WithFinalState()
    {
        var recorder = new DrainRecorder { Decision = DocumentDrainDecision.Keep };
        var (router, store) = BuildRouter(recorder);
        const string docId = "drain-keep-doc";
        const string peerId = "peer-1";

        var revision = await JoinAndEditAsync(router, docId, peerId);

        // Last (only) peer leaves → document drains.
        await router.RemovePeerFromAllSessionsAsync(peerId);

        recorder.Calls.Should().Be(1);
        recorder.Last.Should().NotBeNull();
        recorder.Last!.DocumentId.Should().Be(docId);
        recorder.Last.DocumentType.Should().Be("text");
        recorder.Last.Revision.Should().Be(revision);
        Encoding.UTF8.GetString(recorder.Last.State.Span).Should().Contain("Hello");

        // Keep decision → the document remains durable in storage.
        var snapshot = await store.LoadSnapshotAsync(docId);
        snapshot.Should().NotBeNull();
    }

    [Fact]
    public async Task DrainHandler_ReturningDelete_RemovesAllDocumentData()
    {
        var recorder = new DrainRecorder { Decision = DocumentDrainDecision.Delete };
        var (router, store) = BuildRouter(recorder);
        const string docId = "drain-delete-doc";
        const string peerId = "peer-1";

        await JoinAndEditAsync(router, docId, peerId);

        await router.RemovePeerFromAllSessionsAsync(peerId);

        recorder.Calls.Should().Be(1);

        // Delete decision → all document data is gone and the session is closed.
        var snapshot = await store.LoadSnapshotAsync(docId);
        snapshot.Should().BeNull();
        router.TryGetActiveSession(docId).Should().BeNull();
    }

    [Fact]
    public async Task DrainHandler_NotInvoked_WhileOtherPeersRemain()
    {
        var recorder = new DrainRecorder();
        var (router, _) = BuildRouter(recorder);
        const string docId = "drain-multi-doc";

        await router.JoinDocumentAsync(docId, "text", "peer-1", ProtocolVersions.Current);
        await router.JoinDocumentAsync(docId, "text", "peer-2", ProtocolVersions.Current);

        // First peer leaves — one peer still connected, so no drain yet.
        await router.RemovePeerFromAllSessionsAsync("peer-1");
        recorder.Calls.Should().Be(0);

        // Second (last) peer leaves — now it drains.
        await router.RemovePeerFromAllSessionsAsync("peer-2");
        recorder.Calls.Should().Be(1);
    }
}
