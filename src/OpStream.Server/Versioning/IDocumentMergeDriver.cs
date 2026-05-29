using OpStream.Server.Models;

namespace OpStream.Server.Versioning;

/// <summary>
/// Engine-agnostic contract for the 3-way merge driver.
/// Each engine registers a typed <see cref="DocumentMergeDriver{TDoc,TOp}"/> that implements this.
/// The <see cref="VersioningRouter"/> dispatches to the correct driver via <see cref="MergeDriverRegistry"/>.
/// </summary>
public interface IDocumentMergeDriver
{
    string EngineType { get; }

    Task<MergeReport> MergeAsync(
        string targetPhysicalDocumentId,
        string targetBranchId,
        string sourcePhysicalDocumentId,
        string sourceBranchId,
        TransformPriority priority = TransformPriority.ExistingWins,
        bool dryRun = false,
        CancellationToken ct = default);
}
