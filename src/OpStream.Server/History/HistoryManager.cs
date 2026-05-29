using OpStream.Server.Engine;
using OpStream.Server.Storage;
using OpStream.Shared.Abstractions;
using OpStream.Shared.Messages;
using System.Text.Json;

namespace OpStream.Server.History;

/// <summary>
/// Service responsible for reconstructing historical document states and calculating diffs.
/// </summary>
/// <typeparam name="TDoc">The type of the document.</typeparam>
/// <typeparam name="TOp">The type of the operations.</typeparam>
public class HistoryManager<TDoc, TOp>
{
    private readonly IHistoryStore _historyStore;
    private readonly IOpEngine<TDoc, TOp> _engine;
    private readonly IDocumentSeeder<TDoc> _seeder;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Initializes a new instance of the HistoryManager.
    /// </summary>
    public HistoryManager(IHistoryStore historyStore, IOpEngine<TDoc, TOp> engine, IDocumentSeeder<TDoc> seeder)
    {
        _historyStore = historyStore;
        _engine = engine;
        _seeder = seeder;
        _jsonOptions = new JsonSerializerOptions 
        { 
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    /// <summary>
    /// Reconstructs the document state at a specific revision.
    /// </summary>
    public async Task<TDoc> ReconstructStateAtRevisionAsync(string documentId, long targetRevision, CancellationToken ct = default)
    {
        // 1. Find the nearest snapshot before or at the target revision
        var snapshot = await _historyStore.LoadHistorySnapshotAsync(documentId, targetRevision, ct);
        
        TDoc currentState;
        long startRevision;

        if (snapshot != null)
        {
            currentState = JsonSerializer.Deserialize<TDoc>(snapshot.State.Span, _jsonOptions) 
                ?? throw new InvalidOperationException("Failed to deserialize historical snapshot.");
            startRevision = snapshot.Revision;
        }
        else
        {
            // If no snapshot exists, we assume we must start from the beginning of history (Revision 0)
            currentState = await _seeder.GetInitialStateAsync(documentId, ct)
                ?? throw new InvalidOperationException("Failed to seed initial document state.");
            startRevision = 0;
        }

        // 2. Apply operations from the snapshot revision up to the target revision
        if (startRevision < targetRevision)
        {
            var ops = _historyStore.StreamHistoryOpsAsync(documentId, startRevision, targetRevision, ct);
            await foreach (var storedOp in ops)
            {
                var op = JsonSerializer.Deserialize<TOp>(storedOp.Payload.Span, _jsonOptions);
                if (op != null)
                {
                    currentState = _engine.Apply(currentState, op);
                }
            }
        }

        return currentState;
    }

    /// <summary>
    /// Calculates a "Giga-Op" that represents all changes between two revisions.
    /// </summary>
    public async Task<TOp?> ComposeRangeAsync(string documentId, long fromRevision, long toRevision, CancellationToken ct = default)
    {
        var ops = _historyStore.StreamHistoryOpsAsync(documentId, fromRevision, toRevision, ct);
        
        TOp? gigaOp = default;

        await foreach (var storedOp in ops)
        {
            var op = JsonSerializer.Deserialize<TOp>(storedOp.Payload.Span, _jsonOptions);
            if (op == null) continue;

            if (gigaOp == null)
            {
                gigaOp = op;
            }
            else
            {
                var composed = _engine.Compose(gigaOp, op);
                gigaOp = composed;
            }
        }

        return gigaOp;
    }

    /// <summary>
    /// Gets the milestones for a document.
    /// </summary>
    public Task<IEnumerable<HistoryMilestone>> GetMilestonesAsync(string documentId, CancellationToken ct = default)
    {
        return _historyStore.GetMilestonesAsync(documentId, ct);
    }
}
