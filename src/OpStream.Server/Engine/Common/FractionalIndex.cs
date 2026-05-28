using System.Text;

namespace OpStream.Server.Engine.Common;

/// <summary>
/// Generates lexicographically-comparable string keys used to order siblings.
/// <para>
/// v1: a deliberately small fractional-indexing implementation over a base-95 alphabet
/// (printable ASCII). Good enough for outlines, file trees, and Notion-style block lists
/// where concurrent reorders of the <i>same</i> sibling pair are rare.
/// </para>
/// <para>
/// <b>Known limitations</b> (these are accepted for v1):
/// <list type="bullet">
///   <item>Two peers can independently generate the same key between the same neighbours,
///         producing duplicate positions. Ties are broken at read time by (Position, NodeId),
///         which is enough for convergence but exposes the conflict to UX.</item>
///   <item>Repeated insertions on the same side cause the key length to grow linearly.</item>
/// </list>
/// </para>
/// <para>
/// TODO (v2): replace with a sequence CRDT — RGA or LSEQ — so concurrent inserts at the
/// same gap converge to a deterministic total order without key-length blow-up. The
/// <see cref="TreeNode.Position"/> field will stay a string at the wire level so this
/// migration can be done without breaking serialized documents.
/// </para>
/// </summary>
public static class FractionalIndex
{
    // Printable ASCII range used as our digit alphabet — '!' (0x21) to '~' (0x7E).
    private const char MinChar = (char)0x21;
    private const char MaxChar = (char)0x7E;
    private const char MidChar = (char)((MinChar + MaxChar) / 2);

    /// <summary>
    /// Returns a key strictly between <paramref name="left"/> and <paramref name="right"/>.
    /// Pass <c>null</c> for an open boundary (insert at the start / end of the list).
    /// <para>
    /// Throws <see cref="ArgumentException"/> when the inputs are out of order
    /// (<c>left &gt;= right</c>) OR when the inputs are <i>strictly ordered</i> but no
    /// alphabet-valid intermediate string exists — e.g. <c>Between("a", "a!")</c> where
    /// the next position in <paramref name="right"/> already sits at the alphabet floor.
    /// Callers must react (re-balance neighbouring positions, escalate to a sequence CRDT,
    /// etc.) rather than swallow a silently incorrect key.
    /// </para>
    /// </summary>
    public static string Between(string? left, string? right)
    {
        if (left is not null && right is not null && string.CompareOrdinal(left, right) >= 0)
            throw new ArgumentException($"left ({left}) must be strictly less than right ({right}).");

        var sb = new StringBuilder();
        int i = 0;
        while (true)
        {
            char l = i < (left?.Length ?? 0) ? left![i] : MinChar;
            char r = i < (right?.Length ?? 0) ? right![i] : MaxChar;

            if (l == r)
            {
                sb.Append(l);
                i++;
                continue;
            }

            // There is room between l and r at this digit.
            char mid = (char)((l + r) / 2);
            if (mid != l)
            {
                sb.Append(mid);
                break;
            }

            // l and r are adjacent digits — keep l and descend one more digit to the right.
            sb.Append(l);
            i++;
            // From here on the right boundary is "infinity" (MaxChar) because we already
            // chose the lower digit; we just need any digit strictly greater than MinChar.
            right = null;
        }

        // Defensive guard: there are edge cases (e.g. right = left + MinChar) where no
        // valid intermediate exists in our alphabet. The descent above produces a string
        // that is > right in those cases. Verify and surface the impossibility explicitly.
        var result = sb.ToString();
        if (left is not null && string.CompareOrdinal(left, result) >= 0)
            throw new ArgumentException(
                $"No fractional index fits strictly between \"{left}\" and \"{right}\" (degenerate boundary).");
        if (right is not null && string.CompareOrdinal(result, right) >= 0)
            throw new ArgumentException(
                $"No fractional index fits strictly between \"{left}\" and \"{right}\" (degenerate boundary).");
        return result;
    }
}
