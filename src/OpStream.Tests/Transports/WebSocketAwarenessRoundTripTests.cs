using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpStream.Client.Transports.WebSockets;
using OpStream.Shared.Messages;
using Xunit;
using Xunit.Abstractions;

namespace OpStream.Tests.Transports;

/// <summary>
/// In-process (real Kestrel + real WebSocket endpoint) reproduction of the presence/awareness
/// flow used by the Monaco JS sample.
///
/// Two assertions matter here:
/// <list type="number">
///   <item>The C# <see cref="WebSocketOpStreamClient"/> receives the peer's
///   <see cref="AwarenessState.Data"/> intact via <c>OnReceiveAwareness</c>.</item>
///   <item>The raw JSON the server pushes to a browser uses the property name
///   <c>data</c> (camelCase) — NOT <c>dataJson</c>. The Monaco JS client must read
///   <c>state.data</c>; reading <c>state.dataJson</c> was the bug that hid remote cursors.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public sealed class WebSocketAwarenessRoundTripTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private WebApplication _app = null!;
    private string _wsUrl = "";

    public WebSocketAwarenessRoundTripTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddOpStream()
            .AddWebSocketTransport();

        _app = builder.Build();
        _app.UseWebSockets();
        _app.MapOpStreamWebSockets("/collab-ws");

        await _app.StartAsync();

        var address = _app.Urls.First();
        _wsUrl = address.Replace("http://", "ws://") + "/collab-ws";
        _output.WriteLine($"WebSocket endpoint at {_wsUrl}");
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Awareness_PublishedByPeerA_IsReceivedByPeerB_WithDataIntact()
    {
        const string docId = "ws-presence-doc";

        var peerA = new WebSocketOpStreamClient(
            Options.Create(new OpStreamWebSocketOptions { ServerUri = _wsUrl }));
        var peerB = new WebSocketOpStreamClient(
            Options.Create(new OpStreamWebSocketOptions { ServerUri = _wsUrl }));

        await using var _a = peerA;
        await using var _b = peerB;

        var receivedTcs = new TaskCompletionSource<AwarenessState>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        peerB.OnReceiveAwareness += states =>
        {
            foreach (var s in states) receivedTcs.TrySetResult(s);
            return Task.CompletedTask;
        };

        await peerA.ConnectAndJoinAsync(docId, "text");
        await peerB.ConnectAndJoinAsync(docId, "text");

        var presence = JsonSerializer.SerializeToElement(new
        {
            name = "Bob",
            color = "#3f51b5",
            anchor = 2,
            head = 5,
        });

        await peerA.SendAwarenessAsync(docId, presence);

        var received = await receivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

        received.PeerId.Should().NotBeNullOrEmpty();
        received.Data.ValueKind.Should().Be(JsonValueKind.Object);
        received.Data.GetProperty("name").GetString().Should().Be("Bob");
        received.Data.GetProperty("color").GetString().Should().Be("#3f51b5");
        received.Data.GetProperty("anchor").GetInt32().Should().Be(2);
        received.Data.GetProperty("head").GetInt32().Should().Be(5);
    }

    /// <summary>
    /// Documents the exact wire contract a browser (Monaco) sees: the awareness state is
    /// pushed as <c>receiveAwarenessEvent.awareness[].data</c>. Guards against a regression
    /// to the <c>dataJson</c> property name the JS client previously (incorrectly) read.
    /// </summary>
    [Fact]
    public async Task ReceiveAwarenessEvent_RawJson_UsesCamelCaseDataProperty()
    {
        const string docId = "ws-wire-doc";

        // Raw browser-style listener so we inspect the exact JSON bytes on the wire.
        using var browser = new ClientWebSocket();
        await browser.ConnectAsync(new Uri(_wsUrl), CancellationToken.None);
        await JoinAsync(browser, docId);

        var publisher = new WebSocketOpStreamClient(
            Options.Create(new OpStreamWebSocketOptions { ServerUri = _wsUrl }));
        await using var _pub = publisher;
        await publisher.ConnectAndJoinAsync(docId, "text");

        var presence = JsonSerializer.SerializeToElement(new { name = "Carol", color = "#009688" });
        await publisher.SendAwarenessAsync(docId, presence);

        var json = await WaitForAwarenessFrameAsync(browser, TimeSpan.FromSeconds(10));
        _output.WriteLine(json);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.TryGetProperty("receiveAwarenessEvent", out var evt).Should().BeTrue();
        var first = evt.GetProperty("awareness")[0];

        first.TryGetProperty("data", out var data).Should()
            .BeTrue("the server pushes presence under `data` — the Monaco JS must read state.data");
        first.TryGetProperty("dataJson", out _).Should()
            .BeFalse("there is no `dataJson` field; reading it was the bug that hid remote cursors");
        data.GetProperty("name").GetString().Should().Be("Carol");
    }

    private static async Task JoinAsync(ClientWebSocket ws, string docId)
    {
        var join = new
        {
            correlationId = Guid.NewGuid().ToString(),
            messageType = (int)WebSocketOpMessageType.JoinRequest,
            joinRequest = new { documentId = docId, documentType = "text", clientProtoVersion = 1 },
        };
        await SendAsync(ws, JsonSerializer.Serialize(join));
        // Drain the JoinResponse so it doesn't get confused with later frames.
        await ReceiveAsync(ws, CancellationToken.None);
    }

    private static async Task<string> WaitForAwarenessFrameAsync(ClientWebSocket ws, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            var frame = await ReceiveAsync(ws, cts.Token);
            if (frame.Contains("receiveAwarenessEvent", StringComparison.OrdinalIgnoreCase) ||
                frame.Contains("\"messageType\":6", StringComparison.OrdinalIgnoreCase))
            {
                return frame;
            }
        }
        throw new TimeoutException("No awareness frame received.");
    }

    private static async Task SendAsync(ClientWebSocket ws, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<string> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, ct);
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
