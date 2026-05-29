using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using Microsoft.AspNetCore.SignalR.Client;
using OpStream.Constants;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Xunit;
using Xunit.Abstractions;
using FluentAssertions;
using System.Text.Json;

namespace OpStream.Tests.Backplane;

[Trait("Category", "Integration")]
public class PostgreSqlIntegrationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;

    private INetwork _network = null!;
    private IFutureDockerImage _hostImage = null!;
    private RedisContainer _redis = null!;
    private PostgreSqlContainer _postgres = null!;
    
    // We will start Host A, stop it, then start Host B to prove persistence.
    private IContainer _hostA = null!;
    private IContainer _hostB = null!;

    private ushort _hostAPort;
    private ushort _hostBPort;
    private const int ContainerPort = 8080;

    public PostgreSqlIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _network = new NetworkBuilder().WithName($"opstream-test-{Guid.NewGuid():N}").Build();
        await _network.CreateAsync();

        _redis = new RedisBuilder("redis:latest")
            .WithNetwork(_network)
            .WithNetworkAliases("redis")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli", "ping"))
            .Build();
        await _redis.StartAsync();

        _postgres = new PostgreSqlBuilder("postgres:latest")
            .WithNetwork(_network)
            .WithNetworkAliases("postgres")
            .WithDatabase("opstream")
            .WithUsername("postgres")
            .WithPassword("password")
            .Build();
        await _postgres.StartAsync();

        var repoRoot = FindRepoRoot();
        _hostImage = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(repoRoot)
            .WithDockerfile("Dockerfile")
            .WithName($"opstream-test:{Guid.NewGuid():N}")
            .WithCleanUp(true)
            .Build();
        await _hostImage.CreateAsync();
    }

    private IContainer BuildHostContainer(string alias)
    {
        // Connection string pointing to the postgres container inside the docker network
        var pgConnectionString = "Host=postgres;Port=5432;Database=opstream;Username=postgres;Password=password";

        return new ContainerBuilder(_hostImage)
            .WithNetwork(_network)
            .WithNetworkAliases(alias)
            .WithPortBinding(ContainerPort, true)
            .WithEnvironment("ASPNETCORE_URLS", $"http://+:{ContainerPort}")
            .WithEnvironment("OPSTREAM__TRANSPORTS", "signalr")
            .WithEnvironment("OPSTREAM__ENGINES", "text")
            
            // 👉 HERE WE CONFIGURE POSTGRESQL INSTEAD OF MEMORY
            .WithEnvironment("OPSTREAM__STORAGE__PROVIDER", "postgres")
            .WithEnvironment("OPSTREAM__STORAGE__CONNECTIONSTRING", pgConnectionString)
            
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
        if (_postgres != null) await _postgres.DisposeAsync();
        if (_redis != null) await _redis.DisposeAsync();
        if (_hostImage != null) await _hostImage.DisposeAsync();
        if (_network != null) await _network.DeleteAsync();
    }

    [Fact]
    public async Task DocumentState_IsRestoredFromPostgreSql_AfterNodeCrash()
    {
        var docId = "postgres-doc";

        // ── 1. Start Host A ──
        _hostA = BuildHostContainer("host-a");
        await _hostA.StartAsync();
        _hostAPort = _hostA.GetMappedPublicPort(ContainerPort);
        
        _output.WriteLine("✔ Host A started with PostgreSQL Storage.");

        var hubUrlA = $"http://localhost:{_hostAPort}/collab";
        await using var connectionA = new HubConnectionBuilder().WithUrl(hubUrlA).Build();
        await connectionA.StartAsync();
        
        var joinA = await connectionA.InvokeAsync<JoinResult>(OpStreamConstants.HubMethods.JoinDocument, docId, "text", 1);
        joinA.Revision.Should().Be(1, "this is a brand new document");

        // ── 2. Write an Op to Host A (will be persisted to Postgres) ──
        var textPayload = "Hello DB Persistence";
        var opPayload = JsonSerializer.SerializeToUtf8Bytes(new { Components = new object[] { new { type = "insert", Text = textPayload } } });
        
        var sendRes = await connectionA.InvokeAsync<OpResult>(OpStreamConstants.HubMethods.SendOp, docId, opPayload, joinA.Revision);
        sendRes.Success.Should().BeTrue();
        sendRes.NewRevision.Should().Be(2);

        // Disconnect client
        await connectionA.StopAsync();

        // ── 3. CRASH Host A ──
        _output.WriteLine("💥 Crashing Host A...");
        await _hostA.StopAsync(); // Effectively wiping out all RAM

        // Allow Redis lock TTL to expire if necessary (optional here since it's a new host instance joining)
        await Task.Delay(TimeSpan.FromSeconds(5));

        // ── 4. Start Host B ──
        _hostB = BuildHostContainer("host-b");
        await _hostB.StartAsync();
        _hostBPort = _hostB.GetMappedPublicPort(ContainerPort);

        _output.WriteLine("✔ Host B started. It should reconstruct the document from PostgreSQL.");

        // ── 5. Connect to Host B and verify Snapshot ──
        var hubUrlB = $"http://localhost:{_hostBPort}/collab";
        await using var connectionB = new HubConnectionBuilder().WithUrl(hubUrlB).Build();
        await connectionB.StartAsync();

        var joinB = await connectionB.InvokeAsync<JoinResult>(OpStreamConstants.HubMethods.JoinDocument, docId, "text", 1);
        
        joinB.Revision.Should().Be(2, "Host B should have loaded the operations from Postgres");
        
        // Decode the snapshot to verify the text made it
        var snapshotStr = System.Text.Encoding.UTF8.GetString(joinB.Snapshot);
        snapshotStr.Should().Contain("Hello DB Persistence");

        _output.WriteLine("✅ Document state was successfully recovered from PostgreSQL!");
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
