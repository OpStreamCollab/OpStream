using OpStream.Server.Comments;

namespace OpStream.Server.Session;

/// <summary>
/// Runs once per op after it has been applied to in-memory state and persisted, but BEFORE the
/// broadcast goes out on the backplane. Used by side-effects that must stay consistent with the
/// op log: anchor rebases, audit sinks, derived projections.
/// <para>
/// Hooks run inside the session's serialization lock — keep them cheap, especially CPU-only.
/// </para>
/// </summary>
public interface IPostApplyHook<TOp>
{
    ValueTask<PostApplyResult> AfterApplyAsync(PostApplyContext<TOp> ctx, CancellationToken ct);
}

/// <summary>
/// Context handed to a <see cref="IPostApplyHook{TOp}"/>.
/// </summary>
/// <param name="DocumentId">Global document id (already tenant-scoped).</param>
/// <param name="Revision">The new revision after applying <paramref name="AppliedOp"/>.</param>
/// <param name="AppliedOp">The op as it was applied (post-transform).</param>
/// <param name="PeerId">The peer that authored the op.</param>
/// <param name="IsRehydration"><c>true</c> when called during cold-start replay rather than live application.</param>
public record PostApplyContext<TOp>(
    string DocumentId,
    long Revision,
    TOp AppliedOp,
    string PeerId,
    bool IsRehydration);

/// <summary>
/// Aggregated output of a single hook invocation. Multiple hooks' results are merged by the
/// session before broadcasting.
/// </summary>
public record PostApplyResult(IReadOnlyList<AnchorUpdate>? AnchorUpdates = null)
{
    public static readonly PostApplyResult Empty = new();
}
