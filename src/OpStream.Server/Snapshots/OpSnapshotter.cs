using OpStream.Server.Engine;
using OpStream.Server.Models;
using OpStream.Server.Storage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpStream.Server.Snapshots
{
    /// <summary>
    /// Handles the creation of document snapshots based on defined policies.
    /// </summary>
    public class OpSnapshotter : IOpSnapshotter
    {
        private readonly IDocumentStore _store;
        private readonly IOpHistorySnapshotter _historySnapshotter;
        private readonly ISnapshotPolicy _snapshotPolicy;
        private readonly TimeProvider _timeProvider;

        private int _opsSinceLastSnapshot = 0;
        private long _lastSnapshotTimestamp;

        /// <summary>
        /// Initializes a new instance of the <see cref="OpSnapshotter"/> class.
        /// </summary>
        public OpSnapshotter(IDocumentStore store, ISnapshotPolicy snapshotPolicy, IOpHistorySnapshotter historySnapshotter)
        {
            _store = store;
            _historySnapshotter = historySnapshotter;
            _snapshotPolicy = snapshotPolicy;


            _timeProvider = TimeProvider.System;
            _lastSnapshotTimestamp = _timeProvider.GetTimestamp();
        }

        /// <summary>
        /// Records that an operation was added and checks if a snapshot should be taken.
        /// </summary>
        public async Task<int> OpAddedAsync<T>(T currentState, string documentId, long currentRevision, JsonSerializerOptions jsonOptions, CancellationToken ct)
        {
            int returnValue = 0;

            // Trigger history snapshotting independently
            await _historySnapshotter.OpAddedAsync(currentState, documentId, currentRevision, jsonOptions, ct);

            _opsSinceLastSnapshot++;
            var timeSinceLast = _timeProvider.GetElapsedTime(_lastSnapshotTimestamp);

            SnapshotContext snapshotContext = new SnapshotContext(_opsSinceLastSnapshot, timeSinceLast);

            if (_snapshotPolicy.ShouldTakeSnapshot(snapshotContext))
            {
                returnValue = _opsSinceLastSnapshot;

                await TakeSnapshot(currentState, documentId, currentRevision, jsonOptions, ct);

                _opsSinceLastSnapshot = 0;
                _lastSnapshotTimestamp = _timeProvider.GetTimestamp();
            }

            return returnValue;
        }

       

        /// <summary>
        /// Manually triggers a snapshot of the current state.
        /// </summary>
        public async Task<int> TakeSnapshotAsync<T>(T currentState, string documentId, long currentRevision, JsonSerializerOptions jsonOptions, CancellationToken ct)
        {
            if (_opsSinceLastSnapshot == 0) return 0;


            await TakeSnapshot(currentState, documentId,currentRevision, jsonOptions, ct);

            return _opsSinceLastSnapshot;

        }

        private async Task TakeSnapshot<T>(T currentState, string documentId, long currentRevision, JsonSerializerOptions jsonOptions, CancellationToken ct)
        {
            var stateBytes = JsonSerializer.SerializeToUtf8Bytes(currentState, jsonOptions);
            var snapshot = new DocumentSnapshot(currentRevision, DateTimeOffset.UtcNow, stateBytes);
           
            await _store.WriteSnapshotAsync(documentId, snapshot, ct);
            
            await _store.CompactAsync(documentId, currentRevision, ct);
        }

    }
}
