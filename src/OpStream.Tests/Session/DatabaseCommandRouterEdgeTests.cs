using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using OpStream.Constants;
using OpStream.Server.Multitenancy;
using OpStream.Server.Session;
using OpStream.Server.Storage;
using OpStream.Shared.Abstractions;
using OpStream.Shared.Messages;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OpStream.Tests.Session;

public class DatabaseCommandRouterEdgeTests
{
    private static ServiceProvider BuildProvider(bool allowAuth)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        
        if (allowAuth)
        {
            services.AddOpStream()
                    .UseDatabaseCommandAuthorization<TestAllowAllDatabaseCommandAuthorizer>();
        }
        else
        {
            services.AddOpStream()
                    .UseDatabaseCommandAuthorization<TestDenyAllDatabaseCommandAuthorizer>();
        }

        return services.BuildServiceProvider();
    }

    private class TestAllowAllDatabaseCommandAuthorizer : IDatabaseCommandAuthorizer
    {
        public ValueTask<bool> AuthorizeAsync(DatabaseCommandContext ctx, CancellationToken ct = default)
            => ValueTask.FromResult(true);
    }

    private class TestDenyAllDatabaseCommandAuthorizer : IDatabaseCommandAuthorizer
    {
        public ValueTask<bool> AuthorizeAsync(DatabaseCommandContext ctx, CancellationToken ct = default)
            => ValueTask.FromResult(false);
    }

    [Fact]
    public async Task ListDocumentsAsync_WhenUnauthorized_ShouldFail()
    {
        // Arrange
        var sp = BuildProvider(allowAuth: false);
        var dbRouter = sp.GetRequiredService<DatabaseCommandRouter>();
        await dbRouter.InitializeAsync();

        // Act
        var result = await dbRouter.ListDocumentsAsync(new DocumentQuery());

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Forbidden");
    }

    [Fact]
    public async Task GetDocumentInfoAsync_WhenUnauthorized_ShouldFail()
    {
        // Arrange
        var sp = BuildProvider(allowAuth: false);
        var dbRouter = sp.GetRequiredService<DatabaseCommandRouter>();
        await dbRouter.InitializeAsync();

        // Act
        var result = await dbRouter.GetDocumentInfoAsync("doc-1");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Forbidden");
    }

    [Fact]
    public async Task DeleteDocumentAsync_WhenUnauthorized_ShouldFail()
    {
        // Arrange
        var sp = BuildProvider(allowAuth: false);
        var dbRouter = sp.GetRequiredService<DatabaseCommandRouter>();
        await dbRouter.InitializeAsync();

        // Act
        var result = await dbRouter.DeleteDocumentAsync("doc-1");

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Forbidden");
    }

    [Fact]
    public async Task CompactDocumentAsync_WhenUnauthorized_ShouldFail()
    {
        // Arrange
        var sp = BuildProvider(allowAuth: false);
        var dbRouter = sp.GetRequiredService<DatabaseCommandRouter>();
        await dbRouter.InitializeAsync();

        // Act
        var result = await dbRouter.CompactDocumentAsync("doc-1", 100);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Forbidden");
    }

    [Fact]
    public async Task GetSnapshotAsync_WhenDocumentDoesNotExist_ReturnsNullValue()
    {
        // Arrange
        var sp = BuildProvider(allowAuth: true);
        var dbRouter = sp.GetRequiredService<DatabaseCommandRouter>();
        await dbRouter.InitializeAsync();

        // Act
        var result = await dbRouter.GetSnapshotAsync("non-existent-doc");

        // Assert
        result.Success.Should().BeTrue();
        result.Value.Should().BeNull();
    }
}
