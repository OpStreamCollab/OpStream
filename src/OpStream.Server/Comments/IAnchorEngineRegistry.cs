namespace OpStream.Server.Comments;

/// <summary>
/// Maps the engine-type discriminator string (e.g. "text", "richtext", "json") to the
/// <see cref="IAnchorEngine{TOp}"/> instance that can rebase anchors for that op type.
/// The discriminator is the same string passed to <c>AddEngine&lt;TDoc,TOp,TEngine&gt;(documentType)</c>.
/// </summary>
public interface IAnchorEngineRegistry
{
    /// <summary>
    /// Returns the anchor engine for <paramref name="engineType"/>, or <c>null</c> if the
    /// document type does not support anchored comments.
    /// </summary>
    IAnchorEngineAdapter? TryGet(string engineType);
}

/// <summary>
/// Non-generic adapter that lets the registry invoke a typed <see cref="IAnchorEngine{TOp}"/>
/// without knowing <c>TOp</c> at the call site.
/// </summary>
public interface IAnchorEngineAdapter
{
    AnchorRebaseResult Rebase(Anchor anchor, ReadOnlyMemory<byte> serializedOp);
}
