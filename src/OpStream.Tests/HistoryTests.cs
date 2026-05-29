using OpStream.Server.Engine.Text;
using OpStream.Server.History;
using OpStream.Server.Models;
using OpStream.Server.Storage;
using OpStream.Shared.Messages;
using System.Text.Json;
using Xunit;

namespace OpStream.Tests.History;

public class TestTextDocumentSeeder : OpStream.Shared.Abstractions.IDocumentSeeder<TextDocument>
{
    public ValueTask<TextDocument?> GetInitialStateAsync(string documentId, CancellationToken ct = default)
    {
        return ValueTask.FromResult<TextDocument?>(new TextDocument(""));
    }
}

public class HistoryTests
{
    [Fact]
    public async Task ReconstructStateAtRevisionAsync_WithSnapshotAndOps_ShouldReturnCorrectState()
    {
        // Arrange
        var store = new MemoryDocumentStore();
        var engine = new TextOtEngine();
        var seeder = new TestTextDocumentSeeder();
        var manager = new HistoryManager<TextDocument, TextOp>(store, engine, seeder);
        var docId = "test-doc";

        // Revision 0: Initial state
        var initialState = new TextDocument("");
        var snapshot0 = new DocumentSnapshot(0, DateTimeOffset.UtcNow, JsonSerializer.SerializeToUtf8Bytes(initialState));
        await store.WriteHistorySnapshotAsync(docId, snapshot0);

        // Revision 1: Insert "H"
        var op1 = new TextOp(new List<TextOpComponent> { new Insert("H") });
        var storedOp1 = new StoredOp(1, "user1", DateTimeOffset.UtcNow, JsonSerializer.SerializeToUtf8Bytes(op1), "TextOtEngine");
        await store.AppendHistoryOpAsync(docId, storedOp1);

        // Revision 2: Insert "e"
        var op2 = new TextOp(new List<TextOpComponent> { new Retain(1), new Insert("e") });
        var storedOp2 = new StoredOp(2, "user1", DateTimeOffset.UtcNow, JsonSerializer.SerializeToUtf8Bytes(op2), "TextOtEngine");
        await store.AppendHistoryOpAsync(docId, storedOp2);

        // Snapshot at Revision 2
        var state2 = new TextDocument("He");
        var snapshot2 = new DocumentSnapshot(2, DateTimeOffset.UtcNow, JsonSerializer.SerializeToUtf8Bytes(state2));
        await store.WriteHistorySnapshotAsync(docId, snapshot2);

        // Revision 3: Insert "l"
        var op3 = new TextOp(new List<TextOpComponent> { new Retain(2), new Insert("l") });
        var storedOp3 = new StoredOp(3, "user1", DateTimeOffset.UtcNow, JsonSerializer.SerializeToUtf8Bytes(op3), "TextOtEngine");
        await store.AppendHistoryOpAsync(docId, storedOp3);

        // Act
        var reconstructed = await manager.ReconstructStateAtRevisionAsync(docId, 3);

        // Assert
        Assert.Equal("Hel", reconstructed.Content);
    }

    [Fact]
    public async Task ComposeRangeAsync_ShouldReturnCombinedOperation()
    {
        // Arrange
        var store = new MemoryDocumentStore();
        var engine = new TextOtEngine();
        var seeder = new TestTextDocumentSeeder();
        var manager = new HistoryManager<TextDocument, TextOp>(store, engine, seeder);
        var docId = "test-doc";

        // Op 1: Insert "A"
        var op1 = new TextOp(new List<TextOpComponent> { new Insert("A") });
        await store.AppendHistoryOpAsync(docId, new StoredOp(1, "u", DateTimeOffset.UtcNow, JsonSerializer.SerializeToUtf8Bytes(op1), "TextOtEngine"));

        // Op 2: Insert "B" after "A"
        var op2 = new TextOp(new List<TextOpComponent> { new Retain(1), new Insert("B") });
        await store.AppendHistoryOpAsync(docId, new StoredOp(2, "u", DateTimeOffset.UtcNow, JsonSerializer.SerializeToUtf8Bytes(op2), "TextOtEngine"));

        // Act
        var gigaOp = await manager.ComposeRangeAsync(docId, 0, 2);

        // Assert
        Assert.NotNull(gigaOp);
        var finalState = engine.Apply(new TextDocument(""), gigaOp);
        Assert.Equal("AB", finalState.Content);
    }

    [Fact]
    public async Task ComposeRangeAsync_WithCancellingOperations_ShouldNotThrowException()
    {
        // Arrange
        var store = new MemoryDocumentStore();
        var engine = new TextOtEngine();
        var seeder = new TestTextDocumentSeeder();
        var manager = new HistoryManager<TextDocument, TextOp>(store, engine, seeder);
        var docId = "cancel-test-doc";

        // Op 1: Insert "A"
        var op1 = new TextOp(new List<TextOpComponent> { new Insert("A") });
        await store.AppendHistoryOpAsync(docId, new StoredOp(1, "u", DateTimeOffset.UtcNow, JsonSerializer.SerializeToUtf8Bytes(op1), "TextOtEngine"));

        // Op 2: Delete "A" (the exact text that was inserted)
        var op2 = new TextOp(new List<TextOpComponent> { new Delete(1) });
        await store.AppendHistoryOpAsync(docId, new StoredOp(2, "u", DateTimeOffset.UtcNow, JsonSerializer.SerializeToUtf8Bytes(op2), "TextOtEngine"));

        // Act & Assert
        // Bug: This will THROW NotSupportedException because Compose returns null for cancelled operations
        var gigaOp = await manager.ComposeRangeAsync(docId, 0, 2);

        // If it doesn't throw, it should return null or a NoOp
        if (gigaOp != null)
        {
            Assert.True(engine.IsNoOp(gigaOp));
        }
    }

    [Fact]
    public async Task ReconstructStateAtRevisionAsync_WithoutSnapshot_ShouldNotCrash()
    {
        // Arrange
        var store = new MemoryDocumentStore();
        var engine = new TextOtEngine();
        var seeder = new TestTextDocumentSeeder();
        var manager = new HistoryManager<TextDocument, TextOp>(store, engine, seeder);
        var docId = "no-snapshot-doc";

        // History with Ops but NO Snapshots (which is a valid scenario before snapshot threshold is met)
        // Revision 1: Insert "H"
        var op1 = new TextOp(new List<TextOpComponent> { new Insert("H") });
        var storedOp1 = new StoredOp(1, "user1", DateTimeOffset.UtcNow, JsonSerializer.SerializeToUtf8Bytes(op1), "TextOtEngine");
        await store.AppendHistoryOpAsync(docId, storedOp1);

        // Act & Assert
        // Bug: This will THROW MissingMethodException because TextDocument has no parameterless constructor
        // and HistoryManager uses Activator.CreateInstance<TDoc>() instead of a seeder.
        var reconstructed = await manager.ReconstructStateAtRevisionAsync(docId, 1);

        Assert.NotNull(reconstructed);
        Assert.Equal("H", reconstructed.Content);
    }
}
