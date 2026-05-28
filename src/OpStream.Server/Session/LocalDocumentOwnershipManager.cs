namespace OpStream.Server.Session;

/// <summary>
/// Simple implementation of <see cref="IDocumentOwnershipManager"/> for single-node deployments.
/// Always considers the local node as the owner of any document.
/// </summary>
public class LocalDocumentOwnershipManager : IDocumentOwnershipManager
{
    /// <inheritdoc/>
    public Task<string> GetOrAcquireOwnerAsync(string documentId, string nodeId, CancellationToken ct = default)
    {
        return Task.FromResult(nodeId);
    }

    /// <inheritdoc/>
    public Task<bool> IsOwnerAsync(string documentId, string nodeId, CancellationToken ct = default)
    {
        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public Task ReleaseOwnershipAsync(string documentId, string nodeId, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }
}
