using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpStream.Constants;
using OpStream.Server.Engine;
using OpStream.Server.Engine.Text;
using OpStream.Server.Session;
using OpStream.Server.Snapshots;
using OpStream.Server.Storage;
using OpStream.Shared.Abstractions;
using OpStream.Shared.Messages;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace OpStream.Tests.Session;

public class DocumentSessionEdgeTests
{
    private readonly Mock<IDocumentStore> _storeMock;
    private readonly Mock<IBackplane> _backplaneMock;
    private readonly Mock<IOpSnapshotter> _snapshotterMock;
    private readonly Mock<IOpHistorySnapshotter> _historySnapshotterMock;
    private readonly List<IOpValidator<TextOp>> _validators;
    private readonly NullLogger<DocumentSession<TextDocument, TextOp>> _logger;
    private readonly TextOtEngine _engine;
    private readonly string _documentId = "test-edge-doc";

    public DocumentSessionEdgeTests()
    {
        _storeMock = new Mock<IDocumentStore>();
        _backplaneMock = new Mock<IBackplane>();
        _snapshotterMock = new Mock<IOpSnapshotter>();
        _historySnapshotterMock = new Mock<IOpHistorySnapshotter>();
        _validators = new List<IOpValidator<TextOp>>();
        _logger = NullLogger<DocumentSession<TextDocument, TextOp>>.Instance;
        _engine = new TextOtEngine();

        _backplaneMock.Setup(b => b.NodeId).Returns("edge-node");
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
    public async Task ApplyOpAsync_GapInStream_ShouldFail()
    {
        // Arrange
        var session = CreateSession(new TextDocument("A"), 10);
        var op = TextOp.Create(new Retain(1), new Insert("B"));
        var payload = JsonSerializer.SerializeToUtf8Bytes(op, OpStreamJsonOptions.Default);

        // Client thinks they are at revision 5 (baseRevision = 5), but current is 10.
        // This forces rebase.
        // We simulate a gap by returning ops starting at 7 instead of 6 (expected = baseRevision + 1 = 6).
        var storedOps = new[]
        {
            new StoredOp(7, "peer-2", DateTimeOffset.UtcNow, JsonSerializer.SerializeToUtf8Bytes(TextOp.Create(new Retain(1), new Insert("C")), OpStreamJsonOptions.Default), "TextOp")
        };

        _storeMock.Setup(s => s.StreamOpsAsync(_documentId, 5, It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(storedOps));

        // Act
        var result = await session.ApplyOpAsync("peer-1", payload, 5);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Cannot reconstruct transformation path: compacted log past baseRevision");
    }

    [Fact]
    public async Task ApplyOpAsync_InvalidPayload_ShouldFail()
    {
        // Arrange
        var session = CreateSession();
        var invalidPayload = new byte[] { 0x7B, 0x7D, 0x22 }; // invalid json

        // Act
        var result = await session.ApplyOpAsync("peer-1", invalidPayload, 0);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
        // Should catch the JSON exception
    }

    [Fact]
    public async Task ApplyOpAsync_ValidatorRejects_ShouldFail()
    {
        // Arrange
        var mockValidator = new Mock<IOpValidator<TextOp>>();
        mockValidator.Setup(v => v.ValidateAsync(It.IsAny<OpValidationContext<TextOp>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false); // Reject
        _validators.Add(mockValidator.Object);

        var session = CreateSession();
        var op = TextOp.Create(new Insert("A"));
        var payload = JsonSerializer.SerializeToUtf8Bytes(op, OpStreamJsonOptions.Default);

        // Act
        var result = await session.ApplyOpAsync("peer-1", payload, 0);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Rejected by validator");
    }

    [Fact]
    public async Task ApplyOpAsync_StoreThrows_ShouldFail()
    {
        // Arrange
        var session = CreateSession();
        var op = TextOp.Create(new Insert("A"));
        var payload = JsonSerializer.SerializeToUtf8Bytes(op, OpStreamJsonOptions.Default);

        _storeMock.Setup(s => s.AppendOpAsync(It.IsAny<string>(), It.IsAny<StoredOp>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Store is down"));

        // Act
        var result = await session.ApplyOpAsync("peer-1", payload, 0);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Store is down");
    }

    [Fact]
    public async Task ApplyOpAsync_StoreStreamReturnsFewerOpsThanExpected_ShouldFail()
    {
        // Arrange
        var session = CreateSession(new TextDocument("A"), 10);
        var op = TextOp.Create(new Retain(1), new Insert("B"));
        var payload = JsonSerializer.SerializeToUtf8Bytes(op, OpStreamJsonOptions.Default);

        // baseRevision is 8. Current is 10. Expected stream to return ops 9 and 10.
        // We only return op 9.
        var storedOps = new[]
        {
            new StoredOp(9, "peer-2", DateTimeOffset.UtcNow, JsonSerializer.SerializeToUtf8Bytes(TextOp.Create(new Retain(1), new Insert("C")), OpStreamJsonOptions.Default), "TextOp")
        };

        _storeMock.Setup(s => s.StreamOpsAsync(_documentId, 8, It.IsAny<CancellationToken>()))
            .Returns(ToAsyncEnumerable(storedOps));

        // Act
        var result = await session.ApplyOpAsync("peer-1", payload, 8);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Cannot reconstruct transformation path: op log gap");
    }

    private async IAsyncEnumerable<T> ToAsyncEnumerable<T>(IEnumerable<T> source)
    {
        foreach (var item in source)
        {
            yield return item;
        }
        await Task.CompletedTask;
    }
}
