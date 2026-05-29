using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpStream.Constants;
using OpStream.Server.Engine;
using OpStream.Server.Models;
using OpStream.Server.Session;
using OpStream.Server.Snapshots;
using OpStream.Server.Storage;
using OpStream.Shared.Abstractions;
using System.Text.Json;
using Xunit;

namespace OpStream.Tests.Session;

public class TypedDocumentSessionFactoryTests
{
    [Fact]
    public async Task CreateSessionAsync_WithSnapshot_UsesSnapshotState()
    {
        // Arrange
        var services = new ServiceCollection();
        
        var storeMock = new Mock<IDocumentStore>();
        var seederMock = new Mock<IDocumentSeeder<TestDoc>>();
        var backplaneMock = new Mock<IBackplane>();
        var opSnapshotterMock = new Mock<IOpSnapshotter>();
        var historySnapshotterMock = new Mock<IOpHistorySnapshotter>();
        
        services.AddSingleton(storeMock.Object);
        services.AddSingleton(seederMock.Object);
        services.AddSingleton(backplaneMock.Object);
        services.AddSingleton(opSnapshotterMock.Object);
        services.AddSingleton(historySnapshotterMock.Object);

        var serviceProvider = services.BuildServiceProvider();
        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        var scopeMock = new Mock<IServiceScope>();
        scopeFactoryMock.Setup(s => s.CreateScope()).Returns(scopeMock.Object);
        scopeMock.Setup(s => s.ServiceProvider).Returns(serviceProvider);

        var engineMock = new Mock<IOpEngine<TestDoc, TestOp>>();
        
        var factory = new TypedDocumentSessionFactory<TestDoc, TestOp>(
            "test-type",
            serviceProvider,
            engineMock.Object,
            scopeFactoryMock.Object,
            NullLoggerFactory.Instance);

        var snapshotDoc = new TestDoc { Value = "FromSnapshot" };
        var snapshotData = JsonSerializer.SerializeToUtf8Bytes(snapshotDoc, OpStreamJsonOptions.Default);

        // Act
        var session = await factory.CreateSessionAsync("doc-1", 5, snapshotData, CancellationToken.None);

        // Assert
        session.Should().NotBeNull();
        session.DocumentId.Should().Be("doc-1");
        session.DocumentType.Should().Be("test-type");
        session.CurrentRevision.Should().Be(5);
        
        var stateBytes = session.SerializeState();
        var state = JsonSerializer.Deserialize<TestDoc>(stateBytes.Span, OpStreamJsonOptions.Default);
        state!.Value.Should().Be("FromSnapshot");
        
        // Ensure seeder was NOT called
        seederMock.Verify(s => s.GetInitialStateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateSessionAsync_WithoutSnapshot_CallsSeederAndWritesInitialSnapshot()
    {
        // Arrange
        var services = new ServiceCollection();
        
        var storeMock = new Mock<IDocumentStore>();
        var seederMock = new Mock<IDocumentSeeder<TestDoc>>();
        var backplaneMock = new Mock<IBackplane>();
        var opSnapshotterMock = new Mock<IOpSnapshotter>();
        var historySnapshotterMock = new Mock<IOpHistorySnapshotter>();
        
        var seededDoc = new TestDoc { Value = "FromSeeder" };
        seederMock.Setup(s => s.GetInitialStateAsync("doc-2", It.IsAny<CancellationToken>()))
                  .ReturnsAsync(seededDoc);

        services.AddSingleton(storeMock.Object);
        services.AddSingleton(seederMock.Object);
        services.AddSingleton(backplaneMock.Object);
        services.AddSingleton(opSnapshotterMock.Object);
        services.AddSingleton(historySnapshotterMock.Object);

        var serviceProvider = services.BuildServiceProvider();
        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        var scopeMock = new Mock<IServiceScope>();
        scopeFactoryMock.Setup(s => s.CreateScope()).Returns(scopeMock.Object);
        scopeMock.Setup(s => s.ServiceProvider).Returns(serviceProvider);

        var engineMock = new Mock<IOpEngine<TestDoc, TestOp>>();
        
        var factory = new TypedDocumentSessionFactory<TestDoc, TestOp>(
            "test-type",
            serviceProvider,
            engineMock.Object,
            scopeFactoryMock.Object,
            NullLoggerFactory.Instance);

        // Act
        var session = await factory.CreateSessionAsync("doc-2", 0, null, CancellationToken.None);

        // Assert
        session.Should().NotBeNull();
        session.CurrentRevision.Should().Be(1);
        
        var stateBytes = session.SerializeState();
        var state = JsonSerializer.Deserialize<TestDoc>(stateBytes.Span, OpStreamJsonOptions.Default);
        state!.Value.Should().Be("FromSeeder");

        seederMock.Verify(s => s.GetInitialStateAsync("doc-2", It.IsAny<CancellationToken>()), Times.Once);
        storeMock.Verify(s => s.WriteSnapshotAsync("doc-2", It.Is<DocumentSnapshot>(snap => snap.Revision == 1), It.IsAny<CancellationToken>()), Times.Once);
    }

    public class TestDoc { public string Value { get; set; } = ""; }
    public class TestOp { }
}
