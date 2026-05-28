using OpStream.Server.Models;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpStream.Server.Session.Snapshots;

/// <summary>
/// A no-op implementation of <see cref="IOpHistorySnapshotter"/> that does nothing.
/// </summary>
public class NoopHistorySnapshotter : IOpHistorySnapshotter
{
    /// <inheritdoc/>
    public Task OpAddedAsync<T>(T currentState, string documentId, long currentRevision, JsonSerializerOptions jsonOptions, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task AppendOpAsync(string documentId, StoredOp op, CancellationToken ct)
    {
        return Task.CompletedTask;
    }
}
