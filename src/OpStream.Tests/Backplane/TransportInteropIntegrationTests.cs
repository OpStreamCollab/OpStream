using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using OpStream.Client.Transports.WebSockets;
using OpStream.Constants;
using Testcontainers.Redis;
using Xunit;
using Xunit.Abstractions;
using FluentAssertions;
using System.Text.Json;

namespace OpStream.Tests.Backplane;

[Trait("Category", "Integration")]
public class TransportInteropIntegrationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;

    private INetwork _network = null!;
    private IFutureDockerImage _hostImage = null!;
    private RedisContainer _redis = null!;
    private IContainer _hostA = null!;
    private IContainer _hostB = null!;

    private ushort _hostAPort;
    private ushort _hostBPort;
    private const int ContainerPort = 8080;

    public TransportInteropIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _network = new NetworkBuilder().WithName($"opstream-test-{Guid.NewGuid():N}").Build();
        await _network.CreateAsync();

        _redis = new RedisBuilder("redis:latest").WithNetwork(_network).WithNetworkAliases("redis")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli", "ping")).Build();
        await _redis.StartAsync();

        var repoRoot = FindRepoRoot();
        _hostImage = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(repoRoot)
            .WithDockerfile("Dockerfile")
            .WithName($"opstream-test:{Guid.NewGuid():N}")
            .WithCleanUp(true)
            .Build();
        await _hostImage.CreateAsync();

        _hostA = BuildHostContainer("host-a");
        _hostB = BuildHostContainer("host-b");

        await Task.WhenAll(_hostA.StartAsync(), _hostB.StartAsync());

        _hostAPort = _hostA.GetMappedPublicPort(ContainerPort);
        _hostBPort = _hostB.GetMappedPublicPort(ContainerPort);
    }

    private IContainer BuildHostContainer(string alias)
    {
        // Notice we enable both signalr and websockets here!
        return new ContainerBuilder(_hostImage)
            .WithNetwork(_network)
            .WithNetworkAliases(alias)
            .WithPortBinding(ContainerPort, true)
            .WithEnvironment("ASPNETCORE_URLS", $"http://+:{ContainerPort}")
            .WithEnvironment("OPSTREAM__TRANSPORTS", "signalr,websockets")
            .WithEnvironment("OPSTREAM__ENGINES", "text")
            .WithEnvironment("OPSTREAM__STORAGE__PROVIDER", "memory")
            .WithEnvironment("OPSTREAM__BACKPLANE__PROVIDER", "redis")
            .WithEnvironment("OPSTREAM__BACKPLANE__CONNECTIONSTRING", "redis:6379")
            .WithEnvironment("OPSTREAM__SIGNALR__PATH", "/collab")
            // Configure path for websocket manually (default is /collab-ws)
            .WithEnvironment("WebSockets__Path", "/collab-ws") 
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPath("/health/live").ForPort(ContainerPort)))
            .Build();
    }

    public async Task DisposeAsync()
    {
        if (_hostB != null) await _hostB.DisposeAsync();
        if (_hostA != null) await _hostA.DisposeAsync();
        if (_redis != null) await _redis.DisposeAsync();
        if (_hostImage != null) await _hostImage.DisposeAsync();
        if (_network != null) await _network.DeleteAsync();
    }

    [Fact]
    public async Task SignalR_And_WebSockets_Clients_CanCollaborate_ThroughBackplane()
    {
        var docId = "interop-doc";

        // ── Client 1: SignalR connecting to Host A ──
        var hubUrlA = $"http://localhost:{_hostAPort}/collab";
        await using var signalRClient = new HubConnectionBuilder().WithUrl(hubUrlA).Build();
        
        var signalROpTcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        signalRClient.On<byte[], long>(OpStreamConstants.ClientEvents.ReceiveOp, (payload, rev) => {
            signalROpTcs.TrySetResult(rev);
            return Task.CompletedTask;
        });

        await signalRClient.StartAsync();
        var joinSignalR = await signalRClient.InvokeAsync<JoinResult>(OpStreamConstants.HubMethods.JoinDocument, docId, "text", 1);
        _output.WriteLine("✔ SignalR Client connected and joined.");

        // ── Client 2: WebSockets connecting to Host B ──
        var wsUrlB = $"ws://localhost:{_hostBPort}/collab-ws";
        var wsOptions = Options.Create(new OpStreamWebSocketOptions { ServerUri = wsUrlB });
        var wsClient = new WebSocketOpStreamClient(wsOptions);

        var wsOpTcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        wsClient.OnReceiveOp += (payload, rev) => {
            wsOpTcs.TrySetResult(rev);
            return Task.CompletedTask;
        };

        var joinWs = await wsClient.ConnectAndJoinAsync(docId, "text");
        _output.WriteLine("✔ WebSockets Client connected and joined.");

        // ACT: Send Op from SignalR -> should arrive to WebSockets
        var opPayload1 = JsonSerializer.SerializeToUtf8Bytes(new { Components = new object[] { new { type = "insert", Text = "Hello from SignalR " } } });
        var sendRes1 = await signalRClient.InvokeAsync<OpResult>(OpStreamConstants.HubMethods.SendOp, docId, opPayload1, joinSignalR.Revision);
        sendRes1.Success.Should().BeTrue();

        var receivedWsRev = await wsOpTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        receivedWsRev.Should().Be(sendRes1.NewRevision);
        _output.WriteLine("✅ SignalR -> Host A -> Redis -> Host B -> WebSocket: PASSED");

        // ACT: Send Op from WebSockets -> should arrive to SignalR
        var opPayload2 = JsonSerializer.SerializeToUtf8Bytes(new { Components = new object[] { new { type = "insert", Text = "Hello from WS" } } });
        var sendRes2 = await wsClient.SendOpAsync(docId, opPayload2, receivedWsRev);
        sendRes2.Success.Should().BeTrue();

        var receivedSignalRRev = await signalROpTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        receivedSignalRRev.Should().Be(sendRes2.NewRevision);
        _output.WriteLine("✅ WebSocket -> Host B -> Redis -> Host A -> SignalR: PASSED");
        
        await wsClient.DisposeAsync();
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "Dockerfile"))) return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        var fallback = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        if (File.Exists(Path.Combine(fallback, "Dockerfile"))) return fallback;
        throw new InvalidOperationException("Could not locate repo root.");
    }

    private record JoinResult(long Revision, byte[] Snapshot, List<AwarenessStateDto>? CurrentAwareness);
    private record OpResult(bool Success, long NewRevision, string? ErrorMessage);
    private record AwarenessStateDto(string PeerId, JsonElement? Data);
}
