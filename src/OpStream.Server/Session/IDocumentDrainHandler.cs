namespace OpStream.Server.Session;

/// <summary>
/// Final snapshot of a document handed to every <see cref="IDocumentDrainHandler"/> at the
/// moment its last peer disconnects (the document "drains").
/// </summary>
/// <param name="DocumentId">
/// The id of the document that drained, as seen by the session layer.
/// </param>
/// <param name="DocumentType">
/// The document-type discriminator the document was opened with (e.g. <c>"text"</c>).
/// </param>
/// <param name="Revision">The final accepted revision at drain time.</param>
/// <param name="State">
/// The complete current document state serialized as UTF-8 JSON using
/// <see cref="OpStream.Constants.OpStreamJsonOptions.Default"/>. This is the full document,
/// not a delta — deserialize it with the same options and your document's <c>TDoc</c> type.
/// </param>
/// <param name="DrainedAt">When the document drained (UTC).</param>
public sealed record DocumentDrainContext(
    string DocumentId,
    string DocumentType,
    long Revision,
    ReadOnlyMemory<byte> State,
    DateTimeOffset DrainedAt);

/// <summary>
/// What OpStream should do with a document after its drain handlers have run.
/// </summary>
public enum DocumentDrainDecision
{
    /// <summary>
    /// Keep the document and all its data. The default — the session is simply closed after
    /// the usual idle grace period and can be reopened later.
    /// </summary>
    Keep = 0,

    /// <summary>
    /// Permanently delete the document and all associated data — current state, op log,
    /// snapshots and history — immediately after the drain handlers complete. Typically
    /// returned once the host has durably captured the final state into its own store.
    /// The live session is closed and a cluster-wide eviction is broadcast so no node keeps
    /// stale state.
    /// </summary>
    Delete = 1,
}

/// <summary>
/// Host extension point invoked when a document loses its <b>last</b> peer — i.e. everyone
/// who was collaborating has disconnected and the document goes quiet.
/// <para>
/// Use it to capture the final version of the whole document: persist it into your own
/// database, push it to object storage, enqueue a downstream workflow, and so on. The
/// <see cref="DocumentDrainContext.State"/> is the complete current state at the last
/// accepted revision.
/// </para>
/// <para>
/// The handler returns a <see cref="DocumentDrainDecision"/>. Return
/// <see cref="DocumentDrainDecision.Keep"/> (the default) to leave the document in place, or
/// <see cref="DocumentDrainDecision.Delete"/> to have OpStream permanently remove the
/// document and all of its data once you have safely persisted the final state yourself.
/// </para>
/// <para>
/// Register one or more implementations with
/// <c>builder.AddDocumentDrainHandler&lt;THandler&gt;()</c>. Handlers are resolved in a fresh
/// dependency-injection scope every time a document drains, so they may safely depend on
/// scoped services such as a <c>DbContext</c>. Multiple handlers run in registration order;
/// an exception thrown by one is logged and never prevents the others from running, nor does
/// it interrupt peer-disconnect cleanup. If <em>any</em> handler returns
/// <see cref="DocumentDrainDecision.Delete"/>, the document is deleted.
/// </para>
/// </summary>
/// <remarks>
/// The handler fires once each time the active-peer count transitions to zero. If a peer
/// rejoins later and everyone leaves again, it fires again. It does <em>not</em> fire when a
/// document is removed administratively (delete / purge) — that is a separate event.
/// </remarks>
public interface IDocumentDrainHandler
{
    /// <summary>
    /// Called once when a document's last peer leaves. It is awaited as part of
    /// peer-disconnect cleanup, so keep it reasonably quick — offload long work to a queue or
    /// background channel if needed.
    /// </summary>
    /// <param name="ctx">The final state of the drained document.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>
    /// Whether OpStream should keep or delete the document. Defaults to
    /// <see cref="DocumentDrainDecision.Keep"/> — return <see cref="DocumentDrainDecision.Delete"/>
    /// to have all of the document's data removed.
    /// </returns>
    ValueTask<DocumentDrainDecision> OnDocumentDrainedAsync(DocumentDrainContext ctx, CancellationToken ct = default);
}
