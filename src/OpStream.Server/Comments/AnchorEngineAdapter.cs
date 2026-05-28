using OpStream.Constants;
using System.Text.Json;

namespace OpStream.Server.Comments;

/// <summary>
/// Non-generic bridge between the <see cref="IAnchorEngineRegistry"/> (which only knows an
/// engine-type string) and the typed <see cref="IAnchorEngine{TOp}"/> implementations.
/// </summary>
public sealed class AnchorEngineAdapter<TOp> : IAnchorEngineAdapter
{
    private readonly IAnchorEngine<TOp> _engine;

    public AnchorEngineAdapter(IAnchorEngine<TOp> engine) => _engine = engine;

    public AnchorRebaseResult Rebase(Anchor anchor, ReadOnlyMemory<byte> serializedOp)
    {
        var op = JsonSerializer.Deserialize<TOp>(serializedOp.Span, OpStreamJsonOptions.Default)
            ?? throw new InvalidOperationException($"Failed to deserialize op as {typeof(TOp).Name}.");
        return _engine.Rebase(anchor, op);
    }
}

/// <summary>
/// Default <see cref="IAnchorEngineRegistry"/> backed by registrations added via
/// <c>AddAnchorEngine&lt;TOp&gt;(documentType)</c> on the <see cref="IOpStreamBuilder"/>.
/// </summary>
public sealed class AnchorEngineRegistry : IAnchorEngineRegistry
{
    private readonly IReadOnlyDictionary<string, IAnchorEngineAdapter> _adapters;

    public AnchorEngineRegistry(IEnumerable<AnchorEngineRegistration> registrations)
    {
        _adapters = registrations.ToDictionary(r => r.DocumentType, r => r.Adapter,
            StringComparer.OrdinalIgnoreCase);
    }

    public IAnchorEngineAdapter? TryGet(string engineType)
    {
        _adapters.TryGetValue(engineType, out var adapter);
        return adapter;
    }
}

/// <summary>
/// Carries one document-type → adapter mapping that <see cref="AnchorEngineRegistry"/> aggregates.
/// </summary>
public sealed record AnchorEngineRegistration(string DocumentType, IAnchorEngineAdapter Adapter);
