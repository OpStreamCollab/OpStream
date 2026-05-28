using OpStream.Server.Engine.Json;

namespace OpStream.Server.Comments;

/// <summary>
/// Rebases <c>"json"</c>-kind anchors against <see cref="JsonOpBatch"/> operations.
/// Anchor.Data shape: <c>{ "path": "root.users[3].name" }</c>.
/// <para>
/// The anchor is marked <see cref="AnchorOutcome.Orphaned"/> when a winning
/// <see cref="DeletePropertyOp"/> targets the anchored path or any of its ancestor paths.
/// All other ops leave the anchor <see cref="AnchorOutcome.Unchanged"/> — JSON CRDT paths
/// are stable keys, not positional offsets, so there is no "Moved" outcome for this kind.
/// </para>
/// </summary>
public class JsonPathAnchorEngine : IAnchorEngine<JsonOpBatch>
{
    private const string Kind = "json";

    public AnchorRebaseResult Rebase(Anchor anchor, JsonOpBatch batch)
    {
        if (!string.Equals(anchor.Kind, Kind, StringComparison.Ordinal))
            return new AnchorRebaseResult(anchor, AnchorOutcome.Unchanged);

        if (!anchor.Data.TryGetProperty("path", out var pathProp) ||
            pathProp.ValueKind != System.Text.Json.JsonValueKind.String)
            return new AnchorRebaseResult(anchor, AnchorOutcome.Unchanged);

        var anchoredPath = pathProp.GetString()!;

        foreach (var op in batch.Operations)
        {
            if (op is not DeletePropertyOp del) continue;

            // Orphan if the deletion targets the anchored path or any ancestor.
            if (IsAncestorOrEqual(del.Path, anchoredPath))
                return new AnchorRebaseResult(anchor, AnchorOutcome.Orphaned);
        }

        return new AnchorRebaseResult(anchor, AnchorOutcome.Unchanged);
    }

    /// <summary>
    /// Returns true when <paramref name="deletedPath"/> is equal to or an ancestor of
    /// <paramref name="anchoredPath"/>.
    /// E.g. "root.users" is an ancestor of "root.users[3].name".
    /// </summary>
    private static bool IsAncestorOrEqual(string deletedPath, string anchoredPath)
    {
        if (string.Equals(deletedPath, anchoredPath, StringComparison.Ordinal))
            return true;

        // The anchored path descends from deletedPath when it starts with deletedPath
        // followed immediately by '.' (object child) or '[' (array element).
        if (anchoredPath.Length <= deletedPath.Length) return false;
        if (!anchoredPath.StartsWith(deletedPath, StringComparison.Ordinal)) return false;
        char next = anchoredPath[deletedPath.Length];
        return next == '.' || next == '[';
    }
}
