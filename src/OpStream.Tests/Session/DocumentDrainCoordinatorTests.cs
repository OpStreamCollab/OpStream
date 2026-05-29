using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpStream.Constants;
using OpStream.Server.Session;
using OpStream.Server.Storage;
using OpStream.Shared.Abstractions;
using OpStream.Shared.Messages;
using System.Text.Json;
using Xunit;

namespace OpStream.Tests.Session;

public class DocumentDrainCoordinatorTests
{
    [Fact]
    public async Task NotifyAsync_NoHandlers_ReturnsKeep()
    {
        // Arrange
        var services = new ServiceCollection();
        var serviceProvider = services.BuildServiceProvider();

        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        var scopeMock = new Mock<IServiceScope>();
        scopeFactoryMock.Setup(s => s.CreateScope()).Returns(scopeMock.Object);
        scopeMock.Setup(s => s.ServiceProvider).Returns(serviceProvider);

        var coordinator = new DocumentDrainCoordinator(
            scopeFactoryMock.Object,
            Mock.Of<IBackplane>(),
            Mock.Of<IDocumentOwnershipManager>(),
            NullLogger<DocumentDrainCoordinator>.Instance);

        var sessionMock = new Mock<IDocumentSession>();
        sessionMock.Setup(s => s.DocumentId).Returns("doc-1");

        // Act
        var result = await coordinator.NotifyAsync(sessionMock.Object);

        // Assert
        result.Should().Be(DocumentDrainDecision.Keep);
    }

    [Fact]
    public async Task NotifyAsync_WithDeleteHandler_ReturnsDelete()
    {
        // Arrange
        var services = new ServiceCollection();
        var handler1Mock = new Mock<IDocumentDrainHandler>();
        handler1Mock.Setup(h => h.OnDocumentDrainedAsync(It.IsAny<DocumentDrainContext>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(DocumentDrainDecision.Keep);

        var handler2Mock = new Mock<IDocumentDrainHandler>();
        handler2Mock.Setup(h => h.OnDocumentDrainedAsync(It.IsAny<DocumentDrainContext>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(DocumentDrainDecision.Delete);

        services.AddSingleton(handler1Mock.Object);
        services.AddSingleton(handler2Mock.Object);
        var serviceProvider = services.BuildServiceProvider();

        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        var scopeMock = new Mock<IServiceScope>();
        scopeFactoryMock.Setup(s => s.CreateScope()).Returns(scopeMock.Object);
        scopeMock.Setup(s => s.ServiceProvider).Returns(serviceProvider);

        var coordinator = new DocumentDrainCoordinator(
            scopeFactoryMock.Object,
            Mock.Of<IBackplane>(),
            Mock.Of<IDocumentOwnershipManager>(),
            NullLogger<DocumentDrainCoordinator>.Instance);

        var sessionMock = new Mock<IDocumentSession>();
        sessionMock.Setup(s => s.DocumentId).Returns("doc-1");
        sessionMock.Setup(s => s.DocumentType).Returns("text");
        sessionMock.Setup(s => s.CurrentRevision).Returns(10);
        sessionMock.Setup(s => s.SerializeState()).Returns(new byte[] { 1, 2, 3 });

        // Act
        var result = await coordinator.NotifyAsync(sessionMock.Object);

        // Assert
        result.Should().Be(DocumentDrainDecision.Delete);
        handler1Mock.Verify(h => h.OnDocumentDrainedAsync(It.Is<DocumentDrainContext>(c => c.DocumentId == "doc-1"), It.IsAny<CancellationToken>()), Times.Once);
        handler2Mock.Verify(h => h.OnDocumentDrainedAsync(It.Is<DocumentDrainContext>(c => c.DocumentId == "doc-1"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteDataAsync_DeletesFromStoresAndReleasesOwnership()
    {
        // Arrange
        var services = new ServiceCollection();
        var storeMock = new Mock<IDocumentStore>();
        var historyStoreMock = new Mock<IHistoryStore>();
        
        services.AddSingleton(storeMock.Object);
        services.AddSingleton(historyStoreMock.Object);
        var serviceProvider = services.BuildServiceProvider();

        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        var scopeMock = new Mock<IServiceScope>();
        scopeFactoryMock.Setup(s => s.CreateScope()).Returns(scopeMock.Object);
        scopeMock.Setup(s => s.ServiceProvider).Returns(serviceProvider);

        var backplaneMock = new Mock<IBackplane>();
        backplaneMock.Setup(b => b.NodeId).Returns("node-1");

        var ownershipMock = new Mock<IDocumentOwnershipManager>();

        var coordinator = new DocumentDrainCoordinator(
            scopeFactoryMock.Object,
            backplaneMock.Object,
            ownershipMock.Object,
            NullLogger<DocumentDrainCoordinator>.Instance);

        // Act
        await coordinator.DeleteDataAsync("doc-1");

        // Assert
        storeMock.Verify(s => s.DeleteAsync("doc-1", It.IsAny<CancellationToken>()), Times.Once);
        historyStoreMock.Verify(h => h.DeleteAsync("doc-1", It.IsAny<CancellationToken>()), Times.Once);
        
        backplaneMock.Verify(b => b.PublishAsync(
            OpStreamConstants.ManagementChannels.ClusterBroadcast,
            It.Is<BackplaneMessage>(m => m.Type == OpStreamConstants.BackplaneMessages.DocumentDeleted),
            It.IsAny<CancellationToken>()), Times.Once);

        ownershipMock.Verify(o => o.ReleaseOwnershipAsync("doc-1", "node-1", It.IsAny<CancellationToken>()), Times.Once);
    }
}
