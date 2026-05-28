using FluentAssertions;
using OpStream.Server.Engine.Awareness;
using OpStream.Shared.Messages;
using System.Text.Json;
using Xunit;

namespace OpStream.Tests.Engine;

/// <summary>
/// Edge-case tests for <see cref="AwarenessEngine"/>. Verifies merge / expiry semantics
/// and probes the textual basis of <c>IsNoOp</c>.
/// </summary>
public class AwarenessEngineTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static AwarenessState State(string peer, string json, DateTimeOffset ts)
        => new(peer, Parse(json), ts);

    [Fact]
    public void Merge_LaterTimestampWins()
    {
        var engine = new AwarenessEngine();
        var existing = State("p", "{\"x\":1}", DateTimeOffset.UnixEpoch);
        var incoming = State("p", "{\"x\":2}", DateTimeOffset.UnixEpoch.AddSeconds(1));

        engine.Merge(existing, incoming).Should().Be(incoming);
    }

    [Fact]
    public void Merge_EarlierTimestampLoses()
    {
        var engine = new AwarenessEngine();
        var existing = State("p", "{\"x\":2}", DateTimeOffset.UnixEpoch.AddSeconds(1));
        var incoming = State("p", "{\"x\":1}", DateTimeOffset.UnixEpoch);

        engine.Merge(existing, incoming).Should().Be(existing);
    }

    [Fact]
    public void IsExpired_ExactlyAtTtlBoundary_IsNotExpired()
    {
        var engine = new AwarenessEngine(new AwarenessOptions { Ttl = TimeSpan.FromSeconds(30) });
        var origin = DateTimeOffset.UnixEpoch;
        var st = new AwarenessState("p", Parse("{}"), origin);

        // Exactly Ttl later — strictly inside the window.
        engine.IsExpired(st, origin + TimeSpan.FromSeconds(30)).Should().BeFalse();
    }

    [Fact]
    public void IsExpired_JustPastTtl_IsExpired()
    {
        var engine = new AwarenessEngine(new AwarenessOptions { Ttl = TimeSpan.FromSeconds(30) });
        var origin = DateTimeOffset.UnixEpoch;
        var st = new AwarenessState("p", Parse("{}"), origin);

        engine.IsExpired(st, origin + TimeSpan.FromSeconds(31)).Should().BeTrue();
    }

    /// <summary>
    /// Bug-hypothesis-as-documentation: <c>IsNoOp</c> compares raw JSON text. Two payloads
    /// that are semantically identical but textually different (whitespace / key order)
    /// are NOT coalesced — the broadcast goes through unnecessarily.
    /// <para>
    /// This test asserts the desired semantic (coalesce semantically equal payloads).
    /// It will fail with the current text-based comparison; the fix is to canonicalize
    /// JSON or use <c>JsonElement.DeepEquals</c> (.NET 9+, with a fallback for net8.0).
    /// </para>
    /// </summary>
    [Fact]
    public void IsNoOp_SemanticallyEqualPayloadsWithDifferentFormatting_ShouldBeCoalesced()
    {
        var engine = new AwarenessEngine();
        var existing = State("p", "{\"x\":1,\"y\":2}", DateTimeOffset.UnixEpoch);
        var incoming = State("p", "{ \"x\": 1, \"y\": 2 }", DateTimeOffset.UnixEpoch.AddSeconds(1));

        engine.IsNoOp(existing, incoming).Should().BeTrue(
            "two cursor payloads that differ only in whitespace are the same update");
    }

    /// <summary>
    /// Same payload, different peer — never a no-op (different cursors).
    /// </summary>
    [Fact]
    public void IsNoOp_SamePayloadDifferentPeer_IsNotNoOp()
    {
        var engine = new AwarenessEngine();
        var existing = State("p1", "{\"x\":1}", DateTimeOffset.UnixEpoch);
        var incoming = State("p2", "{\"x\":1}", DateTimeOffset.UnixEpoch.AddSeconds(1));

        engine.IsNoOp(existing, incoming).Should().BeFalse();
    }

    /// <summary>
    /// When coalescing is disabled, even byte-identical updates must pass through.
    /// </summary>
    [Fact]
    public void IsNoOp_CoalescingDisabled_AlwaysReturnsFalse()
    {
        var engine = new AwarenessEngine(new AwarenessOptions { CoalesceIdenticalUpdates = false });
        var existing = State("p", "{\"x\":1}", DateTimeOffset.UnixEpoch);
        var incoming = State("p", "{\"x\":1}", DateTimeOffset.UnixEpoch.AddSeconds(1));

        engine.IsNoOp(existing, incoming).Should().BeFalse();
    }
}
