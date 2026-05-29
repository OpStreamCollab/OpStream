using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpStream.Client.Transports.SignalR;
using OpStream.Shared.Messages;
using Xunit;
using Xunit.Abstractions;

namespace OpStream.Tests.Transports;

/// <summary>
/// In-process (real Kestrel + real SignalR hub) reproduction of the presence/awareness
/// flow used by the Blazorise and Radzen samples. Two real <see cref="SignalROpStreamClient"/>
/// instances join the same document; one publishes awareness and we assert the other
/// receives it via <c>OnReceiveAwareness</c> with the <see cref="AwarenessState.Data"/>
/// payload intact.
///
/// This is the contract the samples depend on to render the remote user's name/cursor.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SignalRAwarenessRoundTripTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private WebApplication _app = null!;
    private string _hubUrl = "";

    public SignalRAwarenessRoundTripTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        // Bind to a random free loopback port.
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddSignalR();
        builder.Services.AddOpStream()
            .AddSignalRTransport();

        _app = builder.Build();
        _app.MapOpStreamSignalR("/collab");

        await _app.StartAsync();

        var address = _app.Urls.First();
        _hubUrl = $"{address}/collab";
        _output.WriteLine($"Hub listening at {_hubUrl}");
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private SignalROpStreamClient NewClient() =>
        new(Options.Create(new OpStreamSignalROptions { HubUrl = _hubUrl }));

    [Fact]
    public async Task Awareness_PublishedByPeerA_IsReceivedByPeerB_WithDataIntact()
    {
        const string docId = "presence-doc";

        await using var peerA = NewClient();
        await using var peerB = NewClient();

        var receivedTcs = new TaskCompletionSource<AwarenessState>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        peerB.OnReceiveAwareness += states =>
        {
            foreach (var s in states) receivedTcs.TrySetResult(s);
            return Task.CompletedTask;
        };

        await peerA.ConnectAndJoinAsync(docId, "text");
        await peerB.ConnectAndJoinAsync(docId, "text");

        // Exactly the shape the Monaco/Blazorise samples broadcast for a remote cursor.
        var presence = JsonSerializer.SerializeToElement(new
        {
            name = "Alice",
            color = "#e91e63",
            anchor = 3,
            head = 7,
        });

        await peerA.SendAwarenessAsync(docId, presence);

        var received = await receivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

        received.PeerId.Should().NotBeNullOrEmpty("the publishing peer must be identified");

        // The crux: Data must survive the server round-trip so the client can render
        // the remote user's name/color/cursor.
        received.Data.ValueKind.Should().Be(JsonValueKind.Object,
            "AwarenessState.Data must arrive as the original JSON object, not null/undefined");
        received.Data.GetProperty("name").GetString().Should().Be("Alice");
        received.Data.GetProperty("color").GetString().Should().Be("#e91e63");
        received.Data.GetProperty("anchor").GetInt32().Should().Be(3);
        received.Data.GetProperty("head").GetInt32().Should().Be(7);
    }
}
