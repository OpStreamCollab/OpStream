using System.Text.Json;

namespace OpStream.Server.Comments;

/// <summary>
/// A reference to a region of a document that follows the content as it changes.
/// <para>
/// The <see cref="Kind"/> discriminator selects which <see cref="IAnchorEngine{TOp}"/>
/// interprets <see cref="Data"/>. Reserved kinds:
/// <list type="bullet">
///   <item><c>"text"</c>     — <c>{ "startOffset": int, "endOffset": int, "biasStart": "left|right", "biasEnd": "left|right" }</c></item>
///   <item><c>"richtext"</c> — same shape as <c>text</c>, applied to the flat text of the Delta.</item>
///   <item><c>"json"</c>     — <c>{ "path": "root.users[3].name" }</c></item>
/// </list>
/// </para>
/// </summary>
public record Anchor(string Kind, JsonElement Data);

/// <summary>
/// Outcome of running an op through <see cref="IAnchorEngine{TOp}.Rebase"/>.
/// </summary>
public enum AnchorOutcome
{
    /// <summary>The op did not affect this anchor.</summary>
    Unchanged,
    /// <summary>The anchor's position shifted but its target is intact.</summary>
    Moved,
    /// <summary>The op destroyed the anchor's target; the comment should be flagged as orphaned.</summary>
    Orphaned
}

/// <summary>
/// Result of a single rebase step.
/// </summary>
public readonly record struct AnchorRebaseResult(Anchor Anchor, AnchorOutcome Outcome);
