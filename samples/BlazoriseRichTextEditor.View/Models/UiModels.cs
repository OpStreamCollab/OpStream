namespace BlazoriseRichTextEditor.Models;

/// <summary>
/// Payload shipped as awareness data — name + color + the user's most recent action.
/// </summary>
public record AwarenessPayload(
    string Name,
    string Color,
    string LastAction,
    DateTimeOffset LastActionAt );

/// <summary>
/// Decoded snapshot of another connected peer.
/// </summary>
public sealed class PeerInfo
{
    public required string PeerId { get; init; }
    public string Name { get; set; } = "anonymous";
    public string Color { get; set; } = "#888";
    public string LastAction { get; set; } = "";
    public DateTimeOffset LastActionAt { get; set; }
}

/// <summary>
/// A single entry in the activity feed shown by the awareness panel.
/// </summary>
public sealed record ActivityEntry( string PeerId, string Name, string Color, string Action, DateTimeOffset At );
