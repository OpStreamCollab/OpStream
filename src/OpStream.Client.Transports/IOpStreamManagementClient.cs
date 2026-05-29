

using OpStream.Server.Models;
using OpStream.Server.Versioning;

namespace OpStream.Client.Transports;

/// <summary>
/// Thrown when a management command fails (authorization denied, not found, etc.).
/// Check <see cref="IsForbidden"/> to distinguish permission errors from other failures.
/// </summary>
public class OpStreamManagementException(string message) : Exception(message)
{
    /// <summary>True when the server returned a "Forbidden:" error.</summary>
    public bool IsForbidden => Message.StartsWith("Forbidden:", StringComparison.Ordinal);
}

/// <summary>
/// Typed management and access client: read and delete documents, history, names, branches and versions.
/// Mirrors <see cref="IOpStreamClient"/> ergonomics but targets the control plane rather than the
/// collaboration path. All ids/names are LOCAL ids; tenant scoping is implicit on the server.
/// </summary>
public interface IOpStreamManagementClient : IAsyncDisposable
{
    /// <summary>Establishes the connection(s) required by the transport.</summary>
    Task ConnectAsync(CancellationToken ct = default);

    // ── Documents / history (DatabaseCommandRouter) ──────────────────────────

    /// <summary>Returns a paged list of documents visible to the current tenant.</summary>
    /// <param name="query">Optional skip/take paging parameters.</param>
    Task<IReadOnlyList<DocumentInfo>> ListDocumentsAsync(DocumentQuery query, CancellationToken ct = default);

    /// <summary>Returns metadata for a single document, or <c>null</c> if not found.</summary>
    /// <param name="documentId">Local document id (no tenant prefix).</param>
    Task<DocumentInfo?> GetDocumentInfoAsync(string documentId, CancellationToken ct = default);

    /// <summary>Returns the latest persisted snapshot for a document, or <c>null</c> if none exists.</summary>
    /// <param name="documentId">Local document id.</param>
    Task<DocumentSnapshot?> GetSnapshotAsync(string documentId, CancellationToken ct = default);

    /// <summary>Returns the named history milestones (including version tags) for a document.</summary>
    /// <param name="documentId">Local document id.</param>
    Task<IReadOnlyList<HistoryMilestone>> ListMilestonesAsync(string documentId, CancellationToken ct = default);

    /// <summary>
    /// Permanently deletes a document and its op log. Evicts any active live session first.
    /// Idempotent — succeeds silently if the document does not exist.
    /// </summary>
    /// <param name="documentId">Local document id.</param>
    Task DeleteDocumentAsync(string documentId, CancellationToken ct = default);

    /// <summary>
    /// Compacts the op log up to <paramref name="upToRevision"/>, respecting the minimum
    /// revision pinned by any version tag.
    /// </summary>
    /// <param name="documentId">Local document id.</param>
    /// <param name="upToRevision">Inclusive upper bound for compaction.</param>
    Task CompactDocumentAsync(string documentId, long upToRevision, CancellationToken ct = default);

    /// <summary>
    /// Purges cold-store history up to <paramref name="upToRevision"/>, respecting the minimum
    /// revision pinned by any version tag.
    /// </summary>
    /// <param name="documentId">Local document id.</param>
    /// <param name="upToRevision">Inclusive upper bound for purge.</param>
    Task PurgeHistoryAsync(string documentId, long upToRevision, CancellationToken ct = default);

    /// <summary>
    /// Deletes every document and history record belonging to the current tenant.
    /// Returns the number of hot-store documents removed. Requires admin-level authorization.
    /// </summary>
    Task<int> PurgeTenantAsync(CancellationToken ct = default);

    // ── Names / branches / versions (VersioningRouter) ───────────────────────

    /// <summary>Returns all registered names visible to the current tenant.</summary>
    Task<IReadOnlyList<DocumentNameInfo>> ListNamesAsync(CancellationToken ct = default);

    /// <summary>
    /// Deletes a registered name. When <paramref name="cascade"/> is <c>false</c> (default) the
    /// call is refused if any branch still exists under that name. When <c>true</c>, all
    /// branches (and their versions) are deleted first, then the name row is removed.
    /// Idempotent — succeeds silently if the name does not exist.
    /// </summary>
    /// <param name="name">Local name to delete.</param>
    /// <param name="cascade">When <c>true</c>, deletes all branches and versions automatically.</param>
    Task DeleteNameAsync(string name, bool cascade = false, CancellationToken ct = default);

    /// <summary>Returns all branches registered under the given name.</summary>
    /// <param name="name">Local name.</param>
    Task<IReadOnlyList<BranchRef>> ListBranchesAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Creates a new branch by forking <paramref name="fromBranchId"/>.
    /// When <paramref name="atRevision"/> is <c>null</c>, the fork point is the latest snapshot
    /// in the hot store; pass a specific revision to fork from cold history.
    /// </summary>
    /// <param name="name">Local name.</param>
    /// <param name="fromBranchId">Source branch id.</param>
    /// <param name="newBranchId">Id for the new branch (must not already exist).</param>
    /// <param name="atRevision">Optional historical revision at which to fork.</param>
    Task<BranchRef> ForkBranchAsync(string name, string fromBranchId, string newBranchId, long? atRevision = null, CancellationToken ct = default);

    /// <summary>
    /// Permanently deletes a branch and its physical op log.
    /// Refused if the branch has child forks. Idempotent — succeeds silently if not found.
    /// </summary>
    /// <param name="name">Local name.</param>
    /// <param name="branchId">Branch to delete.</param>
    Task DeleteBranchAsync(string name, string branchId, CancellationToken ct = default);

    /// <summary>Returns all version tags on the given branch.</summary>
    /// <param name="name">Local name.</param>
    /// <param name="branchId">Branch id.</param>
    Task<IReadOnlyList<VersionRef>> ListVersionsAsync(string name, string branchId, CancellationToken ct = default);

    /// <summary>
    /// Tags the current revision of <paramref name="branchId"/> with an immutable label.
    /// Writes a named history snapshot so the bytes survive future compaction.
    /// </summary>
    /// <param name="name">Local name.</param>
    /// <param name="branchId">Branch id.</param>
    /// <param name="tag">Unique tag label on the branch (e.g. "v1.0").</param>
    Task<VersionRef> CreateVersionAsync(string name, string branchId, string tag, CancellationToken ct = default);

    /// <summary>
    /// Returns the raw snapshot bytes stored for a named version tag, or <c>null</c> if not found.
    /// </summary>
    /// <param name="name">Local name.</param>
    /// <param name="branchId">Branch id.</param>
    /// <param name="tag">Tag label.</param>
    Task<DocumentSnapshot?> ReadVersionSnapshotAsync(string name, string branchId, string tag, CancellationToken ct = default);

    /// <summary>
    /// Removes a version tag, releasing the compaction floor it pinned.
    /// The backing history snapshot is kept by default; pass <paramref name="dropSnapshot"/> = <c>true</c>
    /// to also delete the snapshot bytes (only when the history store supports it).
    /// Idempotent — succeeds silently if the tag does not exist.
    /// </summary>
    /// <param name="name">Local name.</param>
    /// <param name="branchId">Branch id.</param>
    /// <param name="tag">Tag label to remove.</param>
    /// <param name="dropSnapshot">When <c>true</c>, also deletes the backing history snapshot.</param>
    Task DeleteVersionAsync(string name, string branchId, string tag, bool dropSnapshot = false, CancellationToken ct = default);

    /// <summary>
    /// Merges <paramref name="sourceBranchId"/> into <paramref name="targetBranchId"/> using 3-way OT/CRDT.
    /// Pass <paramref name="dryRun"/> = <c>true</c> to preview the merge report without committing any ops.
    /// </summary>
    /// <param name="name">Local name.</param>
    /// <param name="targetBranchId">Branch that receives the merged ops.</param>
    /// <param name="sourceBranchId">Branch whose ops are rebased onto the target.</param>
    /// <param name="dryRun">When <c>true</c>, returns the report without writing anything.</param>
    Task<MergeReport> MergeBranchAsync(string name, string targetBranchId, string sourceBranchId, bool dryRun = false, CancellationToken ct = default);
}
