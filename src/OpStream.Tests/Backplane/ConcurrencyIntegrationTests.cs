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
public class ConcurrencyIntegrationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;

    private INetwork _network = null!;
    private IFutureDockerImage _hostImage = null!;
    private RedisContainer _redis = null!;
    private IContainer _hostA = null!;
    private IContainer _hostB = null!;
    private IContainer _hostC = null!;

    private ushort _hostAPort;
    private ushort _hostBPort;
    private ushort _hostCPort;
    private const int ContainerPort = 8080;

    public ConcurrencyIntegrationTests(ITestOutputHelper output)
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
        _hostC = BuildHostContainer("host-c");

        await Task.WhenAll(_hostA.StartAsync(), _hostB.StartAsync(), _hostC.StartAsync());

        _hostAPort = _hostA.GetMappedPublicPort(ContainerPort);
        _hostBPort = _hostB.GetMappedPublicPort(ContainerPort);
        _hostCPort = _hostC.GetMappedPublicPort(ContainerPort);
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
        if (_hostC != null) await _hostC.DisposeAsync();
        if (_hostB != null) await _hostB.DisposeAsync();
        if (_hostA != null) await _hostA.DisposeAsync();
        if (_redis != null) await _redis.DisposeAsync();
        if (_hostImage != null) await _hostImage.DisposeAsync();
        if (_network != null) await _network.DeleteAsync();
    }

    [Fact]
    public async Task HighConcurrency_OT_ShouldResolveWithoutDataLoss()
    {
        var docId = "concurrency-doc";
        var ports = new[] { _hostAPort, _hostBPort, _hostCPort };
        var numClients = 9; // 3 clients per host
        var opsPerClient = 20;

        var clients = new List<HubConnection>();
        var joinTasks = new List<Task<JoinResult>>();

        _output.WriteLine("🔌 Connecting 9 clients across 3 distributed hosts...");

        for (int i = 0; i < numClients; i++)
        {
            var hostPort = ports[i % 3];
            var url = $"http://localhost:{hostPort}/collab";
            var connection = new HubConnectionBuilder().WithUrl(url).Build();
            clients.Add(connection);
            await connection.StartAsync();

            joinTasks.Add(connection.InvokeAsync<JoinResult>(OpStreamConstants.HubMethods.JoinDocument, docId, "text", 1));
        }

        var joinResults = await Task.WhenAll(joinTasks);

        _output.WriteLine($"🚀 Starting barrage of {numClients * opsPerClient} concurrent operations...");

        var opTasks = new List<Task<OpResult>>();
        for (int i = 0; i < opsPerClient; i++)
        {
            for (int c = 0; c < numClients; c++)
            {
                var connection = clients[c];
                var currentRev = joinResults[c].Revision; // Send relative to when they joined
                var text = $"C{c}-O{i} ";
                var opPayload = JsonSerializer.SerializeToUtf8Bytes(new { Components = new object[] { new { type = "insert", Text = text } } });
                
                opTasks.Add(connection.InvokeAsync<OpResult>(OpStreamConstants.HubMethods.SendOp, docId, opPayload, currentRev));
            }
        }

        // Wait for all operations to be processed by the CRDT/OT engine across nodes
        var results = await Task.WhenAll(opTasks);

        if (results.Any(r => !r.Success))
        {
            var firstError = results.First(r => !r.Success);
            _output.WriteLine($"❌ Some operations failed! First error: {firstError.ErrorMessage}");
        }

        results.All(r => r.Success).Should().BeTrue("all operations should be accepted successfully");

        _output.WriteLine("⏳ Allowing 2 seconds for backplane fanout...");
        await Task.Delay(2000);

        // Disconnect clients to flush
        await Task.WhenAll(clients.Select(c => c.StopAsync()));

        _output.WriteLine("🔍 Verifying final converged state via a neutral connection...");
        // Verify final state by connecting a completely new client to a random host
        await using var validationClient = new HubConnectionBuilder().WithUrl($"http://localhost:{_hostCPort}/collab").Build();
        await validationClient.StartAsync();
        var validationJoin = await validationClient.InvokeAsync<JoinResult>(OpStreamConstants.HubMethods.JoinDocument, docId, "text", 1);
        
        // Expected revision = 1 (initial) + 180 ops = 181
        validationJoin.Revision.Should().Be(1 + (numClients * opsPerClient));
        validationJoin.Snapshot.Should().NotBeNullOrEmpty();
        
        _output.WriteLine($"✅ Final Document Revision: {validationJoin.Revision} (Verified!)");
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
