using OpStream.Server.Models;
using OpStream.Server.Storage;
using OpStream.Shared.Messages;
using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpStream.Server.Snapshots;

/// <summary>
/// Default implementation of <see cref="IOpHistorySnapshotter"/> that creates history snapshots based on configuration.
/// </summary>
public class OpHistorySnapshotter : IOpHistorySnapshotter
{
    private readonly IHistoryStore _historyStore;
    private readonly HistoryOptions _options;
    private readonly TimeProvider _timeProvider;

    private readonly ConcurrentDictionary<string, DocumentHistoryState> _states = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="OpHistorySnapshotter"/> class.
    /// </summary>
    public OpHistorySnapshotter(IHistoryStore historyStore, HistoryOptions options, TimeProvider? timeProvider = null)
    {
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public async Task OpAddedAsync<T>(T currentState, string documentId, long currentRevision, JsonSerializerOptions jsonOptions, CancellationToken ct)
    {
        if (!_options.Enabled) return;

        var state = _states.GetOrAdd(documentId, id => new DocumentHistoryState(_timeProvider.GetTimestamp()));

        Interlocked.Increment(ref state.OpsSinceLastSnapshot);
        var timeSinceLast = _timeProvider.GetElapsedTime(state.LastSnapshotTimestamp);

        bool shouldSnapshot = false;

        if (_options.SnapshotRevisionInterval.HasValue && state.OpsSinceLastSnapshot >= _options.SnapshotRevisionInterval.Value)
        {
            shouldSnapshot = true;
        }

        if (_options.SnapshotInterval.HasValue && timeSinceLast >= _options.SnapshotInterval.Value)
        {
            shouldSnapshot = true;
        }

        if (shouldSnapshot)
        {
            var stateBytes = JsonSerializer.SerializeToUtf8Bytes(currentState, jsonOptions);
            var snapshot = new DocumentSnapshot(currentRevision, DateTimeOffset.UtcNow, stateBytes);
            
            await _historyStore.WriteHistorySnapshotAsync(documentId, snapshot, name: null, ct);

            state.OpsSinceLastSnapshot = 0;
            state.LastSnapshotTimestamp = _timeProvider.GetTimestamp();
        }
    }

    /// <inheritdoc/>
    public Task AppendOpAsync(string documentId, StoredOp op, CancellationToken ct)
    {
        if (!_options.Enabled) return Task.CompletedTask;

        return _historyStore.AppendHistoryOpAsync(documentId, op, ct);
    }

    private class DocumentHistoryState
    {
        public int OpsSinceLastSnapshot;
        public long LastSnapshotTimestamp;

        public DocumentHistoryState(long timestamp)
        {
            LastSnapshotTimestamp = timestamp;
        }
    }
}
