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

[Trait("Category", "Integration")]
public class FailoverIntegrationTests : IAsyncLifetime
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

    public FailoverIntegrationTests(ITestOutputHelper output)
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

_hostA = new ContainerBuilder(_hostImage)
    .WithNetwork(_network)
    .Build();
await _hostA.StartAsync();
_hostAPort = _hostA.GetMappedPublicPort(ContainerPort);

        _hostB = BuildHostContainer("host-b");
        await _hostB.StartAsync();
        _hostBPort = _hostB.GetMappedPublicPort(ContainerPort);
    }

    private IContainer BuildHostContainer(string alias)
    {
        return new ContainerBuilder(_hostImage)
            .WithNetwork(_network)
            .WithNetworkAliases(alias)
            .WithPortBinding(ContainerPort, true)
            .WithEnvironment("ASPNETCORE_URLS", $"http://+:{ContainerPort}")
            .WithEnvironment("OPSTREAM__TRANSPORTS", "signalr")
            .WithEnvironment("OPSTREAM__ENGINES", "text")
            .WithEnvironment("OPSTREAM__STORAGE__PROVIDER", "memory")
            .WithEnvironment("OPSTREAM__BACKPLANE__PROVIDER", "redis")
            .WithEnvironment("OPSTREAM__BACKPLANE__CONNECTIONSTRING", "redis:6379")
            .WithEnvironment("OPSTREAM__SIGNALR__PATH", "/collab")
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
    public async Task Failover_DocumentOwnership_TransfersToHostB_WhenHostACrashes()
    {
        var docId = "failover-doc";
        var hubUrlA = $"http://localhost:{_hostAPort}/collab";
        var hubUrlB = $"http://localhost:{_hostBPort}/collab";

        // Client A connects to Host A
        await using var connectionA = new HubConnectionBuilder().WithUrl(hubUrlA).Build();
        await connectionA.StartAsync();
        var joinA = await connectionA.InvokeAsync<JoinResult>(OpStreamConstants.HubMethods.JoinDocument, docId, "text", 1);
        
        var opPayload = JsonSerializer.SerializeToUtf8Bytes(new { Components = new object[] { new { type = "insert", Text = "HostA" } } });
        var send1 = await connectionA.InvokeAsync<OpResult>(OpStreamConstants.HubMethods.SendOp, docId, opPayload, joinA.Revision);
        send1.Success.Should().BeTrue("Host A should accept the operation");

        _output.WriteLine("✔ Host A acquired document lock and wrote op.");

        // CRASH Host A
        _output.WriteLine("💥 Crashing Host A...");
        await _hostA.StopAsync(); // Force stop

        // RedisDocumentOwnershipManager has a lock TTL, usually Host B will wait or steal it if Host A is dead.
        // Wait 12 seconds to ensure the Redis lock TTL expires (default is usually 10s or 5s).
        await Task.Delay(TimeSpan.FromSeconds(12));

        // Client B connects to Host B
        _output.WriteLine("🔌 Client B connecting to Host B...");
        await using var connectionB = new HubConnectionBuilder().WithUrl(hubUrlB).Build();
        await connectionB.StartAsync();

        // If Host B successfully acquires the lock, Join should succeed.
        var joinB = await connectionB.InvokeAsync<JoinResult>(OpStreamConstants.HubMethods.JoinDocument, docId, "text", 1);
        joinB.Revision.Should().Be(send1.NewRevision, "Host B should have recovered the document state from the shared backplane/memory");

        var opPayload2 = JsonSerializer.SerializeToUtf8Bytes(new { Components = new object[] { new { type = "insert", Text = "HostB" } } });
        var send2 = await connectionB.InvokeAsync<OpResult>(OpStreamConstants.HubMethods.SendOp, docId, opPayload2, joinB.Revision);
        
        send2.Success.Should().BeTrue("Host B should accept operations after failover");
        _output.WriteLine("✅ Failover successful. Host B acquired the lock and continued editing.");
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
