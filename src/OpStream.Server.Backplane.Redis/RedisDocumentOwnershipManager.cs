using StackExchange.Redis;
using OpStream.Server.Session;

namespace OpStream.Server.Backplane.Redis;

/// <summary>
/// Manages document ownership using Redis distributed locks.
/// </summary>
public class RedisDocumentOwnershipManager : IDocumentOwnershipManager
{
    private readonly ConnectionMultiplexer _redis;
    private readonly IDatabase _db;
    private readonly TimeSpan _lockTtl = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisDocumentOwnershipManager"/> class.
    /// </summary>
    /// <param name="connectionString">The Redis connection string.</param>
    public RedisDocumentOwnershipManager(string connectionString)
    {
        _redis = ConnectionMultiplexer.Connect(connectionString);
        _db = _redis.GetDatabase();
    }

    /// <summary>
    /// Gets the current owner of a document or attempts to acquire ownership if it's currently unowned.
    /// </summary>
    /// <param name="documentId">The ID of the document.</param>
    /// <param name="nodeId">The ID of the node requesting ownership.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The ID of the node that owns the document.</returns>
    public async Task<string> GetOrAcquireOwnerAsync(string documentId, string nodeId, CancellationToken ct = default)
    {
        var key = $"OpStream:Owner:{documentId}";
        
        // Try to acquire
        if (await _db.StringSetAsync(key, nodeId, _lockTtl, When.NotExists))
        {
            return nodeId;
        }

        // Already exists, get current owner
        var currentOwner = await _db.StringGetAsync(key);
        return currentOwner.HasValue ? currentOwner.ToString() : nodeId; // Fallback if it expired just now
    }

    /// <summary>
    /// Checks if a node is currently the owner of a document and extends the ownership TTL if it is.
    /// </summary>
    /// <param name="documentId">The ID of the document.</param>
    /// <param name="nodeId">The ID of the node to check.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>True if the node is the owner, false otherwise.</returns>
    public async Task<bool> IsOwnerAsync(string documentId, string nodeId, CancellationToken ct = default)
    {
        var key = $"OpStream:Owner:{documentId}";
        var currentOwner = await _db.StringGetAsync(key);
        
        if (currentOwner == nodeId)
        {
            // Extend TTL
            await _db.KeyExpireAsync(key, _lockTtl);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Releases ownership of a document if the specified node is the current owner.
    /// </summary>
    /// <param name="documentId">The ID of the document.</param>
    /// <param name="nodeId">The ID of the node releasing ownership.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task ReleaseOwnershipAsync(string documentId, string nodeId, CancellationToken ct = default)
    {
        var key = $"OpStream:Owner:{documentId}";
        // Only release if we are the owner
        var currentOwner = await _db.StringGetAsync(key);
        if (currentOwner == nodeId)
        {
            await _db.KeyDeleteAsync(key);
        }
    }
}
