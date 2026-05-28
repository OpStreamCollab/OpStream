using System.Text.Json;

namespace OpStream.Server.Snapshots
{
    public interface IOpSnapshotter
    {
        Task<int> OpAddedAsync<T>(T currentState, string documentId, long currentRevision, JsonSerializerOptions jsonOptions, CancellationToken ct);
        Task<int> TakeSnapshotAsync<T>(T currentState, string documentId, long currentRevision, JsonSerializerOptions jsonOptions, CancellationToken ct);
    }
}