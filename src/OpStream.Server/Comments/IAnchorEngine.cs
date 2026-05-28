namespace OpStream.Server.Comments;

/// <summary>
/// Rebases anchors against operations of type <typeparamref name="TOp"/>.
/// <para>
/// One implementation per op type. Documents whose op type has no registered
/// <see cref="IAnchorEngine{TOp}"/> simply do not support anchored comments —
/// the rebase hook becomes a no-op for them.
/// </para>
/// </summary>
public interface IAnchorEngine<TOp>
{
    /// <summary>
    /// Returns the rebased anchor and the outcome. The <c>bias</c> for offset-based anchors
    /// lives inside <c>anchor.Data</c>; it is not a separate parameter.
    /// </summary>
    AnchorRebaseResult Rebase(Anchor anchor, TOp op);
}
