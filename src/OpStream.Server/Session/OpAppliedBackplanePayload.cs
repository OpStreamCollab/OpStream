using OpStream.Server.Comments;

namespace OpStream.Server.Session;

/// <summary>
/// Wire payload for the <c>op_applied</c> backplane fan-out.
/// </summary>
/// <param name="Operation">The transformed op, serialized as UTF-8 JSON.</param>
/// <param name="NewRevision">The revision after the op was applied.</param>
/// <param name="AnchorUpdates">
/// Optional anchor updates produced by post-apply hooks (e.g. comment anchor rebases).
/// Older clients ignore this field; <em>does not</em> require a protocol version bump.
/// </param>
public record OpAppliedBackplanePayload(
    byte[] Operation,
    long NewRevision,
    IReadOnlyList<AnchorUpdate>? AnchorUpdates = null);
