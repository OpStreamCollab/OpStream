using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpStream.Constants;
using OpStream.Server.Engine;
using OpStream.Server.Engine.Text;
using OpStream.Server.Models;
using OpStream.Server.Session;
using OpStream.Server.Session.Snapshots;
using OpStream.Server.Storage;
using OpStream.Shared.Abstractions;
using System.Text.Json;
using Xunit;

namespace OpStream.Tests.Session;

/// <summary>
/// Comprehensive unit tests for the DocumentSession class.
/// Covers joining, operational transforms, concurrency, and rehydration.
/// </summary>
public class DocumentSessionTests
{
    private readonly Mock<IDocumentStore> _storeMock;
    private readonly Mock<IBackplane> _backplaneMock;
    private readonly Mock<IOpSnapshotter> _snapshotterMock;
    private readonly Mock<IOpHistorySnapshotter> _historySnapshotterMock;
    private readonly List<IOpValidator<TextOp>> _validators;
    private readonly ILogger<DocumentSession<TextDocument, TextOp>> _logger;
    private readonly TextOtEngine _engine;
    private readonly string _documentId = "test-doc";

    public DocumentSessionTests()
    {
        _storeMock = new Mock<IDocumentStore>();
        _backplaneMock = new Mock<IBackplane>();
        _snapshotterMock = new Mock<IOpSnapshotter>();
        _historySnapshotterMock = new Mock<IOpHistorySnapshotter>();
        _validators = new List<IOpValidator<TextOp>>();
        _logger = NullLogger<DocumentSession<TextDocument, TextOp>>.Instance;
        _engine = new TextOtEngine();

        _backplaneMock.Setup(b => b.NodeId).Returns("node-1");
    }

    private DocumentSession<TextDocument, TextOp> CreateSession(TextDocument? initialState = null, long initialRevision = 0)
    {
        return new DocumentSession<TextDocument, TextOp>(
            _documentId,
            initialState ?? new TextDocument(""),
            _engine,
            initialRevision,
            _storeMock.Object,
            _backplaneMock.Object,
            _snapshotterMock.Object,
            _historySnapshotterMock.Object,
            _validators,
            _logger);
    }

    [Fact]
    public async Task JoinAsync_ShouldReturnCorrectCurrentState()
    {
        // Arrange
        var initialState = new TextDocument("Initial Content");
        var session = CreateSession(initialState, 5);

        // Act
        var result = await session.JoinAsync("peer-1");

        // Assert
        result.Revision.Should().Be(5);
        var doc = JsonSerializer.Deserialize<TextDocument>(result.Snapshot.Span, OpStreamJsonOptions.Default);
        doc!.Content.Should().Be("Initial Content");
        session.ActivePeersCount.Should().Be(1);
    }

    [Fact]
    public async Task ApplyOpAsync_SimpleInsert_ShouldSucceedAndPersist()
    {
        // Arrange
        var session = CreateSession(new TextDocument("Hello"), 0);
        var op = TextOp.Create(new Retain(5), new Insert(" World"));
        var payload = JsonSerializer.SerializeToUtf8Bytes(op, OpStreamJsonOptions.Default);

        // Act
        var result = await session.ApplyOpAsync("peer-1", payload, 0);

        // Assert
        result.Success.Should().BeTrue();
        result.NewRevision.Should().Be(1);
        session.CurrentRevision.Should().Be(1);

        _storeMock.Verify(s => s.AppendOpAsync(_documentId, It.Is<StoredOp>(o => o.Revision == 1), It.IsAny<CancellationToken>()), Times.Once);
        _backplaneMock.Verify(b => b.PublishAsync(_documentId, It.Is<BackplaneMessage>(m => m.Type == OpStreamConstants.BackplaneMessages.OpApplied), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApplyOpAsync_ConcurrentOperations_ShouldTransformCorrectly()
    {
        // Arrange
        var session = CreateSession(new TextDocument("A"), 0);
        
        // 1. Peer 1 applies "AB" (Revision 1)
        var op1 = TextOp.Create(new Retain(1), new Insert("B"));
        await session.ApplyOpAsync("peer-1", JsonSerializer.SerializeToUtf8Bytes(op1, OpStreamJsonOptions.Default), 0);

        // 2. Peer 2 sends "AC" based on Revision 0 (Concurrent)
        var op2 = TextOp.Create(new Retain(1), new Insert("C"));
        
        // We need to mock the store to return op1 when session tries to catch up for Peer 2
        var storedOp1 = new StoredOp(1, "peer-1", DateTimeOffset.UtcNow, JsonSerializer.SerializeToUtf8Bytes(op1, OpStreamJsonOptions.Default), "TextOtEngine");
        _storeMock.Setup(s => s.StreamOpsAsync(_documentId, 0, It.IsAny<CancellationToken>()))
                  .Returns(new[] { storedOp1 }.ToAsyncEnumerable());

        // Act
        var result = await session.ApplyOpAsync("peer-2", JsonSerializer.SerializeToUtf8Bytes(op2, OpStreamJsonOptions.Default), 0);

        // Assert
        result.Success.Should().BeTrue();
        result.NewRevision.Should().Be(2);

        // Final state should be "ABC" (ExistingWins priority assumes Peer 1 was already there)
        var joinResult = await session.JoinAsync("inspector");
        var finalDoc = JsonSerializer.Deserialize<TextDocument>(joinResult.Snapshot.Span, OpStreamJsonOptions.Default);
        finalDoc!.Content.Should().Be("ABC");
    }

    [Fact]
    public async Task ApplyOpAsync_ValidatorFails_ShouldRejectOperation()
    {
        // Arrange
        var validatorMock = new Mock<IOpValidator<TextOp>>();
        validatorMock.Setup(v => v.ValidateAsync(It.IsAny<OpValidationContext<TextOp>>(), It.IsAny<CancellationToken>()))
                     .ReturnsAsync(false);
        _validators.Add(validatorMock.Object);

        var session = CreateSession();
        var op = TextOp.Create(new Insert("Malicious"));
        var payload = JsonSerializer.SerializeToUtf8Bytes(op, OpStreamJsonOptions.Default);

        // Act
        var result = await session.ApplyOpAsync("peer-1", payload, 0);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Rejected by validator");
        _storeMock.Verify(s => s.AppendOpAsync(It.IsAny<string>(), It.IsAny<StoredOp>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RehydrateOpAsync_NewRevision_ShouldUpdateState()
    {
        // Arrange
        var session = CreateSession(new TextDocument("Hello"), 0);
        var op = TextOp.Create(new Retain(5), new Insert(" World"));
        var storedOp = new StoredOp(1, "remote-peer", DateTimeOffset.UtcNow, JsonSerializer.SerializeToUtf8Bytes(op, OpStreamJsonOptions.Default), "TextOtEngine");

        // Act
        await session.RehydrateOpAsync(storedOp);

        // Assert
        session.CurrentRevision.Should().Be(1);
        var result = await session.JoinAsync("inspector");
        var doc = JsonSerializer.Deserialize<TextDocument>(result.Snapshot.Span, OpStreamJsonOptions.Default);
        doc!.Content.Should().Be("Hello World");
    }

    [Fact]
    public async Task LeaveAsync_LastPeerLeaves_ShouldTakeSnapshot()
    {
        // Arrange
        var session = CreateSession();
        await session.JoinAsync("peer-1");

        // Act
        await session.LeaveAsync("peer-1");

        // Assert
        session.ActivePeersCount.Should().Be(0);
        _snapshotterMock.Verify(s => s.TakeSnapshotAsync(It.IsAny<TextDocument>(), _documentId, It.IsAny<long>(), It.IsAny<JsonSerializerOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ApplyOpAsync_GapInHistory_ShouldFailWithCompactError()
    {
        // Arrange
        var session = CreateSession(new TextDocument("A"), 2); // Server is at Rev 2
        var op = TextOp.Create(new Insert("B"));
        
        // Peer sends op based on Rev 0, but store only has Rev 2 (Rev 1 was compacted)
        _storeMock.Setup(s => s.StreamOpsAsync(_documentId, 0, It.IsAny<CancellationToken>()))
                  .Returns(new[] { new StoredOp(2, "p", DateTimeOffset.UtcNow, Array.Empty<byte>(), "e") }.ToAsyncEnumerable());

        // Act
        var result = await session.ApplyOpAsync("peer-1", JsonSerializer.SerializeToUtf8Bytes(op, OpStreamJsonOptions.Default), 0);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("compacted log");
    }
}

// Helper to convert IEnumerable to IAsyncEnumerable for mocking StreamOpsAsync
public static class AsyncEnumerableExtensions
{
    public static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(this IEnumerable<T> enumerable)
    {
        foreach (var item in enumerable)
        {
            yield return item;
            await Task.Yield();
        }
    }
}
