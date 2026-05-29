using Microsoft.Extensions.Logging;
using OpStream.Server.Storage;
using OpStream.Shared.Abstractions;

namespace OpStream.Server.Session;

/// <summary>
/// Emits the startup warnings/info about which infrastructure OpStream is running with —
/// flagging non-production defaults (in-memory store, single-node backplane) and listing the
/// registered engines. Separated from the request path so "what do we warn at boot" is its own
/// concern.
/// </summary>
public sealed class OpStreamStartupValidator(
    IDocumentStore store,
    IBackplane backplane,
    IEnumerable<IDocumentSessionFactory> factories,
    ILogger<OpStreamStartupValidator> logger)
{
    /// <summary>Logs the current storage / backplane / engine configuration.</summary>
    public void Validate()
    {
        if (store is MemoryDocumentStore)
            logger.LogWarning(
                "OpStream is using MemoryDocumentStore. All document data will be lost when " +
                "the process restarts. Call UseRedisStorage(), UseEfCoreStorage(), or another " +
                "persistent store before going to production.");
        else
            logger.LogInformation("OpStream storage: {StoreType}", store.GetType().Name);

        if (backplane is LocalBackplane)
            logger.LogInformation(
                "OpStream is running in single-node mode (LocalBackplane). " +
                "Call UseRedisBackplane() or UseNatsBackplane() for multi-node deployments.");
        else
            logger.LogInformation("OpStream backplane: {BackplaneType}", backplane.GetType().Name);

        foreach (var factory in factories)
            logger.LogInformation("OpStream engine registered for document type: \"{DocType}\"", factory.DocumentType);
    }
}
