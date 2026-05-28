using FluentAssertions;
using OpStream.Server.Engine.Common;
using Xunit;

namespace OpStream.Tests.Engine;

/// <summary>
/// Adversarial edge-case tests for <see cref="FractionalIndex.Between"/>.
/// Goal: surface boundary inputs where the algorithm produces a key that violates
/// the strict ordering contract.
/// </summary>
public class FractionalIndexTests
{
    [Fact]
    public void Between_StandardCase_IsStrictlyBetween()
    {
        var mid = FractionalIndex.Between("a", "b");
        string.CompareOrdinal("a", mid).Should().BeLessThan(0);
        string.CompareOrdinal(mid, "b").Should().BeLessThan(0);
    }

    [Fact]
    public void Between_OpenLeft_IsStrictlyLessThanRight()
    {
        var mid = FractionalIndex.Between(null, "m");
        string.CompareOrdinal(mid, "m").Should().BeLessThan(0);
    }

    [Fact]
    public void Between_OpenRight_IsStrictlyGreaterThanLeft()
    {
        var mid = FractionalIndex.Between("m", null);
        string.CompareOrdinal("m", mid).Should().BeLessThan(0);
    }

    /// <summary>
    /// Bug hypothesis: when <paramref name="left"/> is a prefix of <paramref name="right"/>
    /// AND <paramref name="right"/>'s next character is <c>MinChar</c> ('!'), there is no
    /// alphabet-valid string strictly between them — but the current implementation
    /// happily returns one that is actually <b>greater</b> than <paramref name="right"/>.
    /// <para>
    /// Concretely, <c>Between("a", "a!")</c>:
    /// <list type="bullet">
    ///   <item>Any candidate of length ≤ 1 either equals "a" or is &gt; "a!".</item>
    ///   <item>Any candidate of length ≥ 2 with prefix "a" must have second char ≥ '!' = MinChar,
    ///         so it's already ≥ "a!" (equal at length 2, greater at length ≥ 3).</item>
    /// </list>
    /// The method should either throw or return null/empty to signal impossibility.
    /// Returning a key that violates ordering corrupts the sibling list.
    /// </para>
    /// </summary>
    [Fact]
    public void Between_LeftIsPrefixOfRight_WhereRightStartsWithMinChar_MustNotReturnGreaterThanRight()
    {
        // Either throws (acceptable) or returns a value strictly less than the right boundary.
        try
        {
            var mid = FractionalIndex.Between("a", "a!");
            // If it didn't throw, the result must respect strict ordering.
            string.CompareOrdinal("a", mid).Should().BeLessThan(0, "result must be > left");
            string.CompareOrdinal(mid, "a!").Should().BeLessThan(0, "result must be < right; got \"{0}\"", mid);
        }
        catch (ArgumentException)
        {
            // Acceptable: the engine recognized the impossibility.
        }
    }

    /// <summary>
    /// Inputs where left ≥ right are explicitly invalid and must throw.
    /// </summary>
    [Fact]
    public void Between_LeftGreaterOrEqualToRight_Throws()
    {
        var act = () => FractionalIndex.Between("b", "a");
        act.Should().Throw<ArgumentException>();

        var act2 = () => FractionalIndex.Between("a", "a");
        act2.Should().Throw<ArgumentException>();
    }
}
