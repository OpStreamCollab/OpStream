using OpStream.Server.Models;
using System.Text.Json;

namespace OpStream.Server.Session.Snapshots
{
    /// <summary>
    /// Handles the creation of historical snapshots for documents.
    /// </summary>
    public interface IOpHistorySnapshotter
    {
        /// <summary>
        /// Records that an operation was added and checks if a history snapshot should be taken.
        /// </summary>
        Task OpAddedAsync<T>(T currentState, string documentId, long currentRevision, JsonSerializerOptions jsonOptions, CancellationToken ct);

        /// <summary>
        /// Appends an operation to history if enabled.
        /// </summary>
        Task AppendOpAsync(string documentId, StoredOp op, CancellationToken ct);
    }
}
