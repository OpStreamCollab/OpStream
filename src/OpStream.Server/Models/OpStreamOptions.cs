using System;

namespace OpStream.Server.Models;

/// <summary>
/// Options for configuring OpStream.
/// </summary>
public class OpStreamOptions
{
    /// <summary>
    /// Gets or sets the history configuration.
    /// </summary>
    public HistoryOptions History { get; set; } = new();
}

/// <summary>
/// Options for configuring document history.
/// </summary>
public class HistoryOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether history is enabled.
    /// Default is false.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Gets or sets the time interval between history snapshots.
    /// </summary>
    public TimeSpan? SnapshotInterval { get; set; }

    /// <summary>
    /// Gets or sets the number of revisions between history snapshots.
    /// </summary>
    public int? SnapshotRevisionInterval { get; set; }
}
