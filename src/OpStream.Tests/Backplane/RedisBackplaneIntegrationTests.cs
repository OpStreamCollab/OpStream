using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using Microsoft.AspNetCore.SignalR.Client;
using OpStream.Constants;
using Testcontainers.Redis;
using Xunit;
using Xunit.Abstractions;
using FluentAssertions;
using System.Text.Json;

namespace OpStream.Tests.Backplane;

/// <summary>
/// Integration tests for the Redis backplane using Testcontainers.
///
/// Architecture:
///   ┌──────────┐      ┌───────────┐      ┌──────────┐
///   │  Host A  │◄────►│   Redis   │◄────►│  Host B  │
///   │ (SignalR) │      │(Backplane)│      │ (SignalR) │
///   └────┬─────┘      └───────────┘      └────┬─────┘
///        │                                     │
///    Client A                              Client B
///    (sends Op)                         (receives Op)
///
/// Configuration per host:
///   - Storage:   memory
///   - Backplane: redis
///   - Transport: signalr
///   - Engine:    text
/// </summary>
[Trait("Category", "Integration")]
public class RedisBackplaneIntegrationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;

    private INetwork _network = null!;
    private IFutureDockerImage _hostImage = null!;
    private RedisContainer _redis = null!;
    private IContainer _hostA = null!;
    private IContainer _hostB = null!;

    // External ports (mapped at startup)
    private ushort _hostAPort;
    private ushort _hostBPort;

    private const int ContainerPort = 8080;
    private const string DocumentId = "backplane-test-doc";
    private const string DocumentType = "text";

    public RedisBackplaneIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        // ─── 1. Create a shared Docker network ──────────────────────────────
        _network = new NetworkBuilder()
            .WithName($"opstream-test-{Guid.NewGuid():N}")
            .Build();

        await _network.CreateAsync();
        _output.WriteLine($"✔ Network created: {_network.Name}");

        // ─── 2. Start Redis container ───────────────────────────────────────
        _redis = new RedisBuilder("redis:7.4")
            .WithNetwork(_network)
            .WithNetworkAliases("redis")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli", "ping"))
            .Build();

        await _redis.StartAsync();
        _output.WriteLine($"✔ Redis started");

        // ─── 3. Build the OpStream Host image from Dockerfile ───────────────
        // The Dockerfile is at the repo root; the build context is also the repo root
        // because the Dockerfile COPYs from src/.
        var repoRoot = FindRepoRoot();
        _output.WriteLine($"  Build context: {repoRoot}");

        _hostImage = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(repoRoot)
            .WithDockerfile("Dockerfile")
            .WithName($"opstream-test:{Guid.NewGuid():N}")
            .WithCleanUp(true)
            .Build();

        await _hostImage.CreateAsync();
        _output.WriteLine($"✔ Host image built: {_hostImage.FullName}");

        // ─── 4. Start Host A ────────────────────────────────────────────────
        _hostA = new ContainerBuilder(_hostImage)
            .WithNetwork(_network)
            .WithNetworkAliases("host-a")
            .WithPortBinding(ContainerPort, assignRandomHostPort: true)
            .WithEnvironment("ASPNETCORE_URLS", $"http://+:{ContainerPort}")
            .WithEnvironment("OPSTREAM__TRANSPORTS", "signalr")
            .WithEnvironment("OPSTREAM__ENGINES", "text")
            .WithEnvironment("OPSTREAM__STORAGE__PROVIDER", "memory")
            .WithEnvironment("OPSTREAM__BACKPLANE__PROVIDER", "redis")
            .WithEnvironment("OPSTREAM__BACKPLANE__CONNECTIONSTRING", "redis:6379")
            .WithEnvironment("OPSTREAM__SIGNALR__PATH", "/collab")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r
                    .ForPath("/health/live")
                    .ForPort(ContainerPort)))
            .Build();

        await _hostA.StartAsync();
        _hostAPort = _hostA.GetMappedPublicPort(ContainerPort);
        _output.WriteLine($"✔ Host A started on port {_hostAPort}");

        // ─── 5. Start Host B ────────────────────────────────────────────────
        _hostB = new ContainerBuilder(_hostImage)
            .WithNetwork(_network)
            .WithNetworkAliases("host-b")
            .WithPortBinding(ContainerPort, assignRandomHostPort: true)
            .WithEnvironment("ASPNETCORE_URLS", $"http://+:{ContainerPort}")
            .WithEnvironment("OPSTREAM__TRANSPORTS", "signalr")
            .WithEnvironment("OPSTREAM__ENGINES", "text")
            .WithEnvironment("OPSTREAM__STORAGE__PROVIDER", "memory")
            .WithEnvironment("OPSTREAM__BACKPLANE__PROVIDER", "redis")
            .WithEnvironment("OPSTREAM__BACKPLANE__CONNECTIONSTRING", "redis:6379")
            .WithEnvironment("OPSTREAM__SIGNALR__PATH", "/collab")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r
                    .ForPath("/health/live")
                    .ForPort(ContainerPort)))
            .Build();

        await _hostB.StartAsync();
        _hostBPort = _hostB.GetMappedPublicPort(ContainerPort);
        _output.WriteLine($"✔ Host B started on port {_hostBPort}");
    }

    public async Task DisposeAsync()
    {
        // Dispose in reverse order of creation
        if (_hostB != null) await _hostB.DisposeAsync();
        if (_hostA != null) await _hostA.DisposeAsync();
        if (_redis != null) await _redis.DisposeAsync();
        if (_hostImage != null) await _hostImage.DisposeAsync();
        if (_network != null) await _network.DeleteAsync();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  TEST 1: Op sent through Host A is received by client connected to Host B
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that the Redis backplane correctly propagates operations across nodes:
    /// 1. Client A connects to Host A and joins a document
    /// 2. Client B connects to Host B and joins the same document
    /// 3. Client A sends an "insert" operation
    /// 4. Client B should receive the operation via the Redis backplane
    /// </summary>
    [Fact]
    public async Task Op_SentViaHostA_IsReceivedByClientOnHostB()
    {
        // ── Arrange ─────────────────────────────────────────────────────────
        var hubUrlA = $"http://localhost:{_hostAPort}/collab";
        var hubUrlB = $"http://localhost:{_hostBPort}/collab";

        _output.WriteLine($"  Connecting Client A → {hubUrlA}");
        _output.WriteLine($"  Connecting Client B → {hubUrlB}");

        // Build SignalR hub connections
        await using var connectionA = new HubConnectionBuilder()
            .WithUrl(hubUrlA)
            .Build();

        await using var connectionB = new HubConnectionBuilder()
            .WithUrl(hubUrlB)
            .Build();

        // Prepare a TaskCompletionSource to capture the received op on Client B
        var receivedOpTcs = new TaskCompletionSource<(byte[] Payload, long Revision)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        connectionB.On<byte[], long>(OpStreamConstants.ClientEvents.ReceiveOp, (payload, newRevision) =>
        {
            _output.WriteLine($"  ✔ Client B received Op! Revision={newRevision}, PayloadLength={payload.Length}");
            receivedOpTcs.TrySetResult((payload, newRevision));
            return Task.CompletedTask;
        });

        // ── Act ─────────────────────────────────────────────────────────────

        // 1. Start both connections
        await connectionA.StartAsync();
        _output.WriteLine("  Client A connected");

        await connectionB.StartAsync();
        _output.WriteLine("  Client B connected");

        // 2. Both clients join the same document
        var joinResultA = await connectionA.InvokeAsync<JoinResult>(
            OpStreamConstants.HubMethods.JoinDocument,
            DocumentId, DocumentType, 1);
        _output.WriteLine($"  Client A joined document: revision={joinResultA.Revision}");

        var joinResultB = await connectionB.InvokeAsync<JoinResult>(
            OpStreamConstants.HubMethods.JoinDocument,
            DocumentId, DocumentType, 1);
        _output.WriteLine($"  Client B joined document: revision={joinResultB.Revision}");

        // 3. Client A sends an "insert" text operation
        // TextOp uses JsonPolymorphic serialization with "type" discriminator:
        //   {"Components":[{"type":"insert","Text":"Hello"}]}
        var textOp = new
        {
            Components = new object[]
            {
                new { type = "insert", Text = "Hello" }
            }
        };
        var opPayload = JsonSerializer.SerializeToUtf8Bytes(textOp);

        var sendResult = await connectionA.InvokeAsync<OpResult>(
            OpStreamConstants.HubMethods.SendOp,
            DocumentId, opPayload, joinResultA.Revision);

        _output.WriteLine($"  Client A sent Op: Success={sendResult.Success}, NewRevision={sendResult.NewRevision}");
        sendResult.Success.Should().BeTrue("the Op should be applied successfully on Host A");

        // ── Assert ──────────────────────────────────────────────────────────

        // 4. Wait for Client B to receive the operation via the backplane
        //    Timeout of 15 seconds — generous to account for container networking latency
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        cts.Token.Register(() => receivedOpTcs.TrySetCanceled());

        var (receivedPayload, receivedRevision) = await receivedOpTcs.Task;

        receivedRevision.Should().Be(sendResult.NewRevision,
            "the revision received on Host B should match what Host A returned");

        receivedPayload.Should().NotBeNullOrEmpty(
            "the payload should be forwarded through the Redis backplane");

        _output.WriteLine($"  ✅ Backplane test PASSED — Op propagated from Host A → Redis → Host B");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  TEST 2: Awareness update sent through Host A is received by Host B
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Awareness_SentViaHostA_IsReceivedByClientOnHostB()
    {
        // ── Arrange ─────────────────────────────────────────────────────────
        var hubUrlA = $"http://localhost:{_hostAPort}/collab";
        var hubUrlB = $"http://localhost:{_hostBPort}/collab";

        await using var connectionA = new HubConnectionBuilder().WithUrl(hubUrlA).Build();
        await using var connectionB = new HubConnectionBuilder().WithUrl(hubUrlB).Build();

        var receivedAwarenessTcs = new TaskCompletionSource<AwarenessStateDto>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        connectionB.On<AwarenessStateDto>(OpStreamConstants.ClientEvents.ReceiveAwarenessUpdate, (state) =>
        {
            receivedAwarenessTcs.TrySetResult(state);
            return Task.CompletedTask;
        });

        // ── Act ─────────────────────────────────────────────────────────────
        await connectionA.StartAsync();
        await connectionB.StartAsync();

        await connectionA.InvokeAsync<JoinResult>(OpStreamConstants.HubMethods.JoinDocument, DocumentId, DocumentType, 1);
        await connectionB.InvokeAsync<JoinResult>(OpStreamConstants.HubMethods.JoinDocument, DocumentId, DocumentType, 1);

        var jsonStr = """{"cursor": {"index": 5}}""";
        var jsonDoc = JsonDocument.Parse(jsonStr);

        await connectionA.InvokeAsync(OpStreamConstants.HubMethods.UpdateAwareness, DocumentId, jsonDoc.RootElement);

        // ── Assert ──────────────────────────────────────────────────────────
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        cts.Token.Register(() => receivedAwarenessTcs.TrySetCanceled());

        var receivedState = await receivedAwarenessTcs.Task;

        receivedState.PeerId.Should().Be(connectionA.ConnectionId);
        receivedState.Data.HasValue.Should().BeTrue();
        receivedState.Data!.Value.GetProperty("cursor").GetProperty("index").GetInt32().Should().Be(5);
        
        _output.WriteLine($"  ✅ Backplane test PASSED — Awareness propagated");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  TEST 3: Peer disconnect on Host A is broadcast to Host B
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PeerDisconnect_OnHostA_IsBroadcastToClientOnHostB()
    {
        // ── Arrange ─────────────────────────────────────────────────────────
        var hubUrlA = $"http://localhost:{_hostAPort}/collab";
        var hubUrlB = $"http://localhost:{_hostBPort}/collab";

        await using var connectionA = new HubConnectionBuilder().WithUrl(hubUrlA).Build();
        await using var connectionB = new HubConnectionBuilder().WithUrl(hubUrlB).Build();

        var peerDisconnectedTcs = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        connectionB.On<string>(OpStreamConstants.ClientEvents.PeerDisconnected, (peerId) =>
        {
            peerDisconnectedTcs.TrySetResult(peerId);
            return Task.CompletedTask;
        });

        // ── Act ─────────────────────────────────────────────────────────────
        await connectionA.StartAsync();
        await connectionB.StartAsync();

        await connectionA.InvokeAsync<JoinResult>(OpStreamConstants.HubMethods.JoinDocument, DocumentId, DocumentType, 1);
        await connectionB.InvokeAsync<JoinResult>(OpStreamConstants.HubMethods.JoinDocument, DocumentId, DocumentType, 1);

        var peerIdA = connectionA.ConnectionId;

        // Disconnect Client A
        await connectionA.StopAsync(); 

        // ── Assert ──────────────────────────────────────────────────────────
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        cts.Token.Register(() => peerDisconnectedTcs.TrySetCanceled());

        var disconnectedPeerId = await peerDisconnectedTcs.Task;

        disconnectedPeerId.Should().Be(peerIdA);
        
        _output.WriteLine($"  ✅ Backplane test PASSED — Peer disconnect propagated");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  TEST 4: Comment created on Host A is received by Host B
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Comment_CreatedViaHostA_IsReceivedByClientOnHostB()
    {
        // ── Arrange ─────────────────────────────────────────────────────────
        var hubUrlA = $"http://localhost:{_hostAPort}/collab";
        var hubUrlB = $"http://localhost:{_hostBPort}/collab";

        await using var connectionA = new HubConnectionBuilder().WithUrl(hubUrlA).Build();
        await using var connectionB = new HubConnectionBuilder().WithUrl(hubUrlB).Build();

        var commentCreatedTcs = new TaskCompletionSource<OpStream.Client.Transports.CommentDto>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        connectionB.On<OpStream.Client.Transports.CommentDto>(OpStreamConstants.ClientEvents.ReceiveCommentCreated, (comment) =>
        {
            commentCreatedTcs.TrySetResult(comment);
            return Task.CompletedTask;
        });

        // ── Act ─────────────────────────────────────────────────────────────
        await connectionA.StartAsync();
        await connectionB.StartAsync();

        await connectionA.InvokeAsync<JoinResult>(OpStreamConstants.HubMethods.JoinDocument, DocumentId, DocumentType, 1);
        await connectionB.InvokeAsync<JoinResult>(OpStreamConstants.HubMethods.JoinDocument, DocumentId, DocumentType, 1);

        var anchorData = JsonDocument.Parse("""{"start": 0, "end": 5}""").RootElement;
        var newCmd = new OpStream.Client.Transports.NewCommentCmd(
            "Test comment via backplane", 
            new OpStream.Client.Transports.AnchorDto("text", anchorData), 
            null
        );

        var createdComment = await connectionA.InvokeAsync<OpStream.Client.Transports.CommentDto>(
            OpStreamConstants.HubMethods.CreateComment, 
            DocumentId, 
            newCmd
        );

        // ── Assert ──────────────────────────────────────────────────────────
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        cts.Token.Register(() => commentCreatedTcs.TrySetCanceled());

        var receivedComment = await commentCreatedTcs.Task;

        receivedComment.Id.Should().Be(createdComment.Id);
        receivedComment.Body.Should().Be("Test comment via backplane");
        receivedComment.AuthorPeerId.Should().Be(connectionA.ConnectionId);
        
        _output.WriteLine($"  ✅ Backplane test PASSED — Comment created propagated");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  TEST 5: Comment edited on Host A is received by Host B
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Comment_EditedViaHostA_IsReceivedByClientOnHostB()
    {
        // ── Arrange ─────────────────────────────────────────────────────────
        var hubUrlA = $"http://localhost:{_hostAPort}/collab";
        var hubUrlB = $"http://localhost:{_hostBPort}/collab";

        await using var connectionA = new HubConnectionBuilder().WithUrl(hubUrlA).Build();
        await using var connectionB = new HubConnectionBuilder().WithUrl(hubUrlB).Build();

        var commentUpdatedTcs = new TaskCompletionSource<OpStream.Client.Transports.CommentDto>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        connectionB.On<OpStream.Client.Transports.CommentDto>(OpStreamConstants.ClientEvents.ReceiveCommentUpdated, (comment) =>
        {
            commentUpdatedTcs.TrySetResult(comment);
            return Task.CompletedTask;
        });

        // ── Act ─────────────────────────────────────────────────────────────
        await connectionA.StartAsync();
        await connectionB.StartAsync();

        await connectionA.InvokeAsync<JoinResult>(OpStreamConstants.HubMethods.JoinDocument, DocumentId, DocumentType, 1);
        await connectionB.InvokeAsync<JoinResult>(OpStreamConstants.HubMethods.JoinDocument, DocumentId, DocumentType, 1);

        var anchorData = JsonDocument.Parse("""{"start": 0, "end": 5}""").RootElement;
        var newCmd = new OpStream.Client.Transports.NewCommentCmd(
            "Original body", 
            new OpStream.Client.Transports.AnchorDto("text", anchorData), 
            null
        );

        var createdComment = await connectionA.InvokeAsync<OpStream.Client.Transports.CommentDto>(
            OpStreamConstants.HubMethods.CreateComment, 
            DocumentId, 
            newCmd
        );

        // Edit the comment
        await connectionA.InvokeAsync<OpStream.Client.Transports.CommentDto>(
            OpStreamConstants.HubMethods.EditComment, 
            DocumentId, 
            createdComment.Id,
            "Edited body"
        );

        // ── Assert ──────────────────────────────────────────────────────────
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        cts.Token.Register(() => commentUpdatedTcs.TrySetCanceled());

        var receivedComment = await commentUpdatedTcs.Task;

        receivedComment.Id.Should().Be(createdComment.Id);
        receivedComment.Body.Should().Be("Edited body");
        
        _output.WriteLine($"  ✅ Backplane test PASSED — Comment updated propagated");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  TEST 6: Comment deleted on Host A is received by Host B
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Comment_DeletedViaHostA_IsReceivedByClientOnHostB()
    {
        // ── Arrange ─────────────────────────────────────────────────────────
        var hubUrlA = $"http://localhost:{_hostAPort}/collab";
        var hubUrlB = $"http://localhost:{_hostBPort}/collab";

        await using var connectionA = new HubConnectionBuilder().WithUrl(hubUrlA).Build();
        await using var connectionB = new HubConnectionBuilder().WithUrl(hubUrlB).Build();

        var commentDeletedTcs = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        connectionB.On<string>(OpStreamConstants.ClientEvents.ReceiveCommentDeleted, (commentId) =>
        {
            commentDeletedTcs.TrySetResult(commentId);
            return Task.CompletedTask;
        });

        // ── Act ─────────────────────────────────────────────────────────────
        await connectionA.StartAsync();
        await connectionB.StartAsync();

        await connectionA.InvokeAsync<JoinResult>(OpStreamConstants.HubMethods.JoinDocument, DocumentId, DocumentType, 1);
        await connectionB.InvokeAsync<JoinResult>(OpStreamConstants.HubMethods.JoinDocument, DocumentId, DocumentType, 1);

        var anchorData = JsonDocument.Parse("""{"start": 0, "end": 5}""").RootElement;
        var newCmd = new OpStream.Client.Transports.NewCommentCmd(
            "To be deleted", 
            new OpStream.Client.Transports.AnchorDto("text", anchorData), 
            null
        );

        var createdComment = await connectionA.InvokeAsync<OpStream.Client.Transports.CommentDto>(
            OpStreamConstants.HubMethods.CreateComment, 
            DocumentId, 
            newCmd
        );

        // Delete the comment
        await connectionA.InvokeAsync(
            OpStreamConstants.HubMethods.DeleteComment, 
            DocumentId, 
            createdComment.Id
        );

        // ── Assert ──────────────────────────────────────────────────────────
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        cts.Token.Register(() => commentDeletedTcs.TrySetCanceled());

        var receivedCommentId = await commentDeletedTcs.Task;

        receivedCommentId.Should().Be(createdComment.Id);
        
        _output.WriteLine($"  ✅ Backplane test PASSED — Comment deleted propagated");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Walks up from the current directory until it finds the Dockerfile (repo root).
    /// </summary>
    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "Dockerfile")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }

        // Fallback: assume conventional path from test bin output
        // bin/Debug/net9.0 → src/OpStream.Tests → src → repo root
        var fallback = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        if (File.Exists(Path.Combine(fallback, "Dockerfile")))
            return fallback;

        throw new InvalidOperationException(
            "Could not locate the repository root (Dockerfile not found). " +
            $"Searched from: {AppContext.BaseDirectory}");
    }

    // ─── DTOs for SignalR invocation results ─────────────────────────────────

    private record JoinResult(long Revision, byte[] Snapshot, List<AwarenessStateDto>? CurrentAwareness);
    private record OpResult(bool Success, long NewRevision, string? ErrorMessage);
    private record AwarenessStateDto(string PeerId, JsonElement? Data);
}
