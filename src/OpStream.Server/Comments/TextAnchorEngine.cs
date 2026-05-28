using System.Text.Json;
using OpStream.Server.Engine.Text;

namespace OpStream.Server.Comments;

/// <summary>
/// Rebases <c>"text"</c>-kind anchors against <see cref="TextOp"/> operations.
/// Anchor.Data shape: <c>{ "startOffset", "endOffset", "biasStart", "biasEnd" }</c>
/// (biases: <c>"left"</c> or <c>"right"</c>, default <c>"right"</c>).
/// </summary>
public class TextAnchorEngine : IAnchorEngine<TextOp>
{
    public AnchorRebaseResult Rebase(Anchor anchor, TextOp op)
    {
        if (!string.Equals(anchor.Kind, "text", StringComparison.Ordinal))
            return new AnchorRebaseResult(anchor, AnchorOutcome.Unchanged);

        var data = anchor.Data;
        int start = data.GetProperty("startOffset").GetInt32();
        int end = data.GetProperty("endOffset").GetInt32();
        var biasStart = ReadBias(data, "biasStart");
        var biasEnd = ReadBias(data, "biasEnd");

        int origStart = start;
        int origEnd = end;
        int cursor = 0;
        bool destroyed = false;

        foreach (var component in op.Components)
        {
            switch (component)
            {
                case Retain r:
                    cursor += r.Count;
                    break;

                case Insert ins:
                    {
                        int len = ins.Text.Length;
                        start = ShiftForInsert(start, cursor, len, biasStart);
                        end = ShiftForInsert(end, cursor, len, biasEnd);
                        cursor += len;
                        break;
                    }

                case Delete del:
                    {
                        int delStart = cursor;
                        int delEnd = cursor + del.Count;

                        // Detect destruction of the *entire* anchored region.
                        if (delStart <= origStart && delEnd >= origEnd && origEnd > origStart)
                            destroyed = true;

                        start = ShiftForDelete(start, delStart, delEnd);
                        end = ShiftForDelete(end, delStart, delEnd);
                        // cursor stays at delStart (deleted text is gone from the new doc).
                        break;
                    }
            }
        }

        if (destroyed)
            return new AnchorRebaseResult(anchor, AnchorOutcome.Orphaned);

        if (start == origStart && end == origEnd)
            return new AnchorRebaseResult(anchor, AnchorOutcome.Unchanged);

        var newData = JsonSerializer.SerializeToElement(new
        {
            startOffset = start,
            endOffset = end,
            biasStart = biasStart == Bias.Left ? "left" : "right",
            biasEnd = biasEnd == Bias.Left ? "left" : "right",
        });
        return new AnchorRebaseResult(new Anchor(anchor.Kind, newData), AnchorOutcome.Moved);
    }

    private enum Bias { Left, Right }

    private static Bias ReadBias(JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            return Bias.Right;
        return prop.GetString() == "left" ? Bias.Left : Bias.Right;
    }

    private static int ShiftForInsert(int offset, int insertAt, int len, Bias bias)
    {
        if (offset < insertAt) return offset;
        if (offset == insertAt && bias == Bias.Left) return offset;   // stick to left side
        return offset + len;
    }

    private static int ShiftForDelete(int offset, int delStart, int delEnd)
    {
        if (offset <= delStart) return offset;
        if (offset >= delEnd) return offset - (delEnd - delStart);
        return delStart; // offset was inside the deleted range → collapse to its left edge
    }
}
