namespace OpStream.Server.Session;

public interface IDocumentOwnershipManager
{
    Task<string> GetOrAcquireOwnerAsync(string documentId, string nodeId, CancellationToken ct = default);
    Task<bool> IsOwnerAsync(string documentId, string nodeId, CancellationToken ct = default);
    Task ReleaseOwnershipAsync(string documentId, string nodeId, CancellationToken ct = default);
}
