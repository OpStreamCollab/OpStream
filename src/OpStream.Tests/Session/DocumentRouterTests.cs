using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using OpStream.Constants;
using OpStream.Server.Engine.Text;
using OpStream.Server.Session;
using OpStream.Shared.Abstractions;
using System.Text.Json;
using Xunit;

namespace OpStream.Tests.Session;

public class DocumentRouterTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOpStream(); 
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task JoinDocumentAsync_ValidRequest_CreatesSession()
    {
        // Arrange
        var sp = BuildProvider();
        var router = sp.GetRequiredService<DocumentRouter>();
        await router.InitializeAsync();

        // Act
        var result = await router.JoinDocumentAsync("doc-join", "text", "peer-1", ProtocolVersions.Current);

        // Assert
        result.Success.Should().BeTrue();
        result.Value.Should().NotBeNull();
        router.GetActiveDocumentIds().Should().Contain("doc-join");
        router.GetDocumentsId("peer-1").Should().Contain("doc-join");
    }

    [Fact]
    public async Task JoinDocumentAsync_InvalidProtocol_ReturnsFail()
    {
        // Arrange
        var sp = BuildProvider();
        var router = sp.GetRequiredService<DocumentRouter>();
        await router.InitializeAsync();

        // Act
        var result = await router.JoinDocumentAsync("doc-join", "text", "peer-1", 999);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("UnsupportedProtocol");
    }

    [Fact]
    public async Task ApplyOpAsync_ValidOp_UpdatesSession()
    {
        // Arrange
        var sp = BuildProvider();
        var router = sp.GetRequiredService<DocumentRouter>();
        await router.InitializeAsync();

        var join = await router.JoinDocumentAsync("doc-op", "text", "peer-1", ProtocolVersions.Current);
        
        var op = new TextOp(new TextOpComponent[] { new Insert("Hello") });
        var payload = JsonSerializer.SerializeToUtf8Bytes(op, OpStreamJsonOptions.Default);

        // Act
        var result = await router.ApplyOpAsync("peer-1", "doc-op", payload, join.Value!.Revision);

        // Assert
        result.Success.Should().BeTrue();
        result.Value!.NewRevision.Should().BeGreaterThan(join.Value!.Revision);
    }

    [Fact]
    public async Task UpdateAwarenessAsync_ValidData_UpdatesAwareness()
    {
        // Arrange
        var sp = BuildProvider();
        var router = sp.GetRequiredService<DocumentRouter>();
        await router.InitializeAsync();

        await router.JoinDocumentAsync("doc-aware", "text", "peer-1", ProtocolVersions.Current);
        
        var data = JsonDocument.Parse("{\"cursor\": 5}").RootElement;

        // Act
        var result = await router.UpdateAwarenessAsync("peer-1", "doc-aware", data);

        // Assert
        result.Success.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.PeerId.Should().Be("peer-1");
    }

    [Fact]
    public async Task RemovePeerFromAllSessionsAsync_EvictsPeer()
    {
        // Arrange
        var sp = BuildProvider();
        var router = sp.GetRequiredService<DocumentRouter>();
        await router.InitializeAsync();

        await router.JoinDocumentAsync("doc-remove-1", "text", "peer-1", ProtocolVersions.Current);
        await router.JoinDocumentAsync("doc-remove-2", "text", "peer-1", ProtocolVersions.Current);
        
        // Act
        await router.RemovePeerFromAllSessionsAsync("peer-1");

        // Assert
        router.GetDocumentsId("peer-1").Should().BeEmpty();
    }
}
