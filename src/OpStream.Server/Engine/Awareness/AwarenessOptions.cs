namespace OpStream.Server.Engine.Awareness;

/// <summary>
/// Tunables for the awareness pipeline (presence/cursors/selections).
/// </summary>
public sealed class AwarenessOptions
{
    /// <summary>
    /// Inactivity threshold after which a peer's state is considered stale and evicted
    /// from <see cref="GetStates"/>/<see cref="Snapshot"/> reads. Defaults to 30 seconds,
    /// matching the previous hard-coded behavior.
    /// </summary>
    public TimeSpan Ttl { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When <c>true</c>, redundant updates whose payload is byte-equal to the current
    /// stored state for the same peer are suppressed (not broadcast, not re-stored).
    /// </summary>
    public bool CoalesceIdenticalUpdates { get; init; } = true;
}
