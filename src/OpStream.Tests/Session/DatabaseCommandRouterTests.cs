using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using OpStream.Constants;
using OpStream.Server.Session;
using OpStream.Server.Storage;
using OpStream.Shared.Abstractions;
using OpStream.Shared.Messages;
using System.Text.Json;
using Xunit;

namespace OpStream.Tests.Session;

public class DatabaseCommandRouterTests
{
    private static ServiceProvider BuildProvider(Mock<IBackplane>? backplaneMock = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOpStream(); // Adds in-memory stores, local backplane, router, etc.
        
        if (backplaneMock != null)
        {
            // Replace the local backplane with a mock for specific tests
            services.AddSingleton<IBackplane>(backplaneMock.Object);
        }

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task GetDocumentInfoAsync_ReturnsInfo_WhenDocumentExists()
    {
        // Arrange
        var sp = BuildProvider();
        var documentRouter = sp.GetRequiredService<DocumentRouter>();
        await documentRouter.InitializeAsync();
        
        var dbRouter = sp.GetRequiredService<DatabaseCommandRouter>();
        await dbRouter.InitializeAsync();

        var joinResult = await documentRouter.JoinDocumentAsync("doc-1", "text", "peer-1", ProtocolVersions.Current);
        joinResult.Success.Should().BeTrue();

        // Act
        var result = await dbRouter.GetDocumentInfoAsync("doc-1");

        // Assert
        result.Success.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.DocumentId.Should().Be("doc-1");
    }

    [Fact]
    public async Task ListDocumentsAsync_ReturnsCreatedDocuments()
    {
        // Arrange
        var sp = BuildProvider();
        var documentRouter = sp.GetRequiredService<DocumentRouter>();
        await documentRouter.InitializeAsync();
        var dbRouter = sp.GetRequiredService<DatabaseCommandRouter>();
        await dbRouter.InitializeAsync();

        await documentRouter.JoinDocumentAsync("doc-A", "text", "peer-1", ProtocolVersions.Current);
        await documentRouter.JoinDocumentAsync("doc-B", "text", "peer-1", ProtocolVersions.Current);

        // Act
        var result = await dbRouter.ListDocumentsAsync(new DocumentQuery());

        // Assert
        result.Success.Should().BeTrue();
        result.Value.Should().HaveCountGreaterOrEqualTo(2);
        result.Value!.Select(d => d.DocumentId).Should().Contain(new[] { "doc-A", "doc-B" });
    }

    [Fact]
    public async Task DeleteDocumentAsync_EvictsSessionAndDeletesData()
    {
        // Arrange
        var sp = BuildProvider();
        var documentRouter = sp.GetRequiredService<DocumentRouter>();
        await documentRouter.InitializeAsync();
        var dbRouter = sp.GetRequiredService<DatabaseCommandRouter>();
        await dbRouter.InitializeAsync();

        await documentRouter.JoinDocumentAsync("doc-delete", "text", "peer-1", ProtocolVersions.Current);
        documentRouter.GetActiveDocumentIds().Should().Contain("doc-delete");

        // Act
        var result = await dbRouter.DeleteDocumentAsync("doc-delete");

        // Assert
        result.Success.Should().BeTrue();
        documentRouter.GetActiveDocumentIds().Should().NotContain("doc-delete");
        
        var info = await dbRouter.GetDocumentInfoAsync("doc-delete");
        info.Value.Should().BeNull(); // Data is deleted
    }

    [Fact]
    public async Task PurgeTenantAsync_EvictsAllAndDeletesData()
    {
        // Arrange
        var sp = BuildProvider();
        var documentRouter = sp.GetRequiredService<DocumentRouter>();
        await documentRouter.InitializeAsync();
        var dbRouter = sp.GetRequiredService<DatabaseCommandRouter>();
        await dbRouter.InitializeAsync();

        await documentRouter.JoinDocumentAsync("doc-tenant-1", "text", "peer-1", ProtocolVersions.Current);
        await documentRouter.JoinDocumentAsync("doc-tenant-2", "text", "peer-1", ProtocolVersions.Current);
        
        documentRouter.GetActiveDocumentIds().Count.Should().BeGreaterThanOrEqualTo(2);

        // Act
        var result = await dbRouter.PurgeTenantAsync();

        // Assert
        result.Success.Should().BeTrue();
        result.Value.Should().BeGreaterThanOrEqualTo(2); // Number of docs deleted
        
        // Broadcast handler should have evicted sessions
        documentRouter.GetActiveDocumentIds().Should().NotContain("doc-tenant-1");
        documentRouter.GetActiveDocumentIds().Should().NotContain("doc-tenant-2");
    }
}
