using OpStream.Server.Engine.Ephemeral;
using OpStream.Shared.Messages;
using System.Text.Json;

namespace OpStream.Server.Engine.Awareness;

/// <summary>
/// Pure engine that defines the merge/expiry/coalescing semantics of presence
/// (cursors, selections, "user is typing", …).
/// <para>
/// Strategy is last-writer-wins per peer with a configurable TTL — the same
/// behavior that <c>AwarenessManager</c> implemented inline, now extracted so
/// it can be unit-tested in isolation and reused by typed wrappers.
/// </para>
/// </summary>
public sealed class AwarenessEngine : IEphemeralEngine<AwarenessState>
{
    private readonly AwarenessOptions _options;

    public AwarenessEngine(AwarenessOptions? options = null)
    {
        _options = options ?? new AwarenessOptions();
    }

    public AwarenessState Merge(AwarenessState? existing, AwarenessState incoming)
    {
        if (existing is null) return incoming;
        return incoming.LastUpdated >= existing.LastUpdated ? incoming : existing;
    }

    public bool IsExpired(AwarenessState state, DateTimeOffset now)
        => now - state.LastUpdated > _options.Ttl;

    public bool IsNoOp(AwarenessState? existing, AwarenessState incoming)
    {
        if (!_options.CoalesceIdenticalUpdates) return false;
        if (existing is null) return false;
        if (!string.Equals(existing.PeerId, incoming.PeerId, StringComparison.Ordinal)) return false;

        // Structural equality so two payloads that differ only in whitespace / key order /
        // numeric formatting (42 vs 42.0) collapse to the same broadcast. Hand-rolled
        // because JsonElement.DeepEquals is .NET 9-only and we multi-target net8.0/net9.0.
        return JsonDeepEquals(existing.Data, incoming.Data);
    }

    /// <summary>
    /// Structural equality for <see cref="JsonElement"/>: objects compared key-set
    /// independent of order, arrays in order, numbers as decimal values, primitives
    /// by their typed value.
    /// </summary>
    private static bool JsonDeepEquals(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) return false;

        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
            {
                int aCount = 0;
                foreach (var _ in a.EnumerateObject()) aCount++;
                int bCount = 0;
                foreach (var _ in b.EnumerateObject()) bCount++;
                if (aCount != bCount) return false;

                foreach (var aProp in a.EnumerateObject())
                {
                    if (!b.TryGetProperty(aProp.Name, out var bVal)) return false;
                    if (!JsonDeepEquals(aProp.Value, bVal)) return false;
                }
                return true;
            }
            case JsonValueKind.Array:
            {
                using var aEnum = a.EnumerateArray().GetEnumerator();
                using var bEnum = b.EnumerateArray().GetEnumerator();
                while (true)
                {
                    bool aHas = aEnum.MoveNext();
                    bool bHas = bEnum.MoveNext();
                    if (aHas != bHas) return false;
                    if (!aHas) return true;
                    if (!JsonDeepEquals(aEnum.Current, bEnum.Current)) return false;
                }
            }
            case JsonValueKind.String:
                return string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Number:
                // Compare as decimal when possible; fall back to raw-text equality for
                // numbers that don't fit decimal (very large doubles).
                if (a.TryGetDecimal(out var ad) && b.TryGetDecimal(out var bd))
                    return ad == bd;
                if (a.TryGetDouble(out var adb) && b.TryGetDouble(out var bdb))
                    return adb == bdb;
                return string.Equals(a.GetRawText(), b.GetRawText(), StringComparison.Ordinal);
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return true;
            default:
                return string.Equals(a.GetRawText(), b.GetRawText(), StringComparison.Ordinal);
        }
    }
}
