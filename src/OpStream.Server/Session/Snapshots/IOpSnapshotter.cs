using System.Text.Json;

namespace OpStream.Server.Session.Snapshots
{
    public interface IOpSnapshotter
    {
        Task<int> OpAddedAsync<T>(T currentState, string documentId, long currentRevision, JsonSerializerOptions jsonOptions, CancellationToken ct);
        Task<int> TakeSnapshotAsync<T>(T currentState, string documentId, long currentRevision, JsonSerializerOptions jsonOptions, CancellationToken ct);
    }
}