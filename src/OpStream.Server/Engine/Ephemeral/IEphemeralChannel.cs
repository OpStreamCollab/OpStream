namespace OpStream.Server.Engine.Ephemeral;

/// <summary>
/// Transport contract for ephemeral state across the cluster.
/// Abstracts the backplane + serialization concerns so that the
/// <see cref="IEphemeralEngine{TState}"/> stays pure.
/// </summary>
public interface IEphemeralChannel<TState>
{
    /// <summary>
    /// Broadcasts <paramref name="state"/> on the given <paramref name="topic"/>
    /// (typically the document id) attributed to <paramref name="peerId"/>.
    /// </summary>
    Task PublishAsync(string topic, string peerId, TState state, CancellationToken ct = default);
}
