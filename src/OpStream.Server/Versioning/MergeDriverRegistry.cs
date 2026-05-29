namespace OpStream.Server.Versioning;

/// <summary>
/// Maps engine-type strings to their <see cref="IDocumentMergeDriver"/> so the
/// <see cref="VersioningRouter"/> can dispatch merge requests without knowing the type parameters.
/// </summary>
public class MergeDriverRegistry(IEnumerable<IDocumentMergeDriver> drivers)
{
    private readonly Dictionary<string, IDocumentMergeDriver> _map =
        drivers.ToDictionary(d => d.EngineType, StringComparer.OrdinalIgnoreCase);

    public IDocumentMergeDriver? Get(string engineType)
        => _map.GetValueOrDefault(engineType);
}
