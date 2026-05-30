using System;
using OpStream.Server.Session;
using OpStream.Server.Validation;

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

    /// <summary>
    /// Gets or sets the in-memory session registry configuration (e.g. the idle-close timeout).
    /// </summary>
    public SessionRegistryOptions Sessions { get; set; } = new();

    /// <summary>
    /// Gets or sets the inbound message validation limits enforced by the built-in
    /// <see cref="DefaultInboundMessageValidator"/> (payload sizes, id lengths, etc.).
    /// </summary>
    public InboundValidationOptions Validation { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether database migrations should be applied automatically on startup.
    /// Default is true.
    /// </summary>
    public bool AutomaticMigrationsEnabled { get; set; } = true;
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
