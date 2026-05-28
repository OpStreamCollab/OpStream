using System.Text;
using FluentAssertions;
using OpStream.Server.Engine.Text;
using OpStream.Server.Models;
using Xunit;

namespace OpStream.Tests.Engine;

/// <summary>
/// Fuzz tests for the TextOtEngine to ensure convergence in random scenarios.
/// </summary>
public class TextOtFuzzerTests
{
    private readonly TextOtEngine _engine = new();
    private readonly Random _random = new(Seed: 42); // Fixed seed so it's reproducible if it fails

    /// <summary>
    /// Performs a fuzz test with two peers generating random concurrent operations and verifies they always converge.
    /// </summary>
    [Fact]
    public void FuzzTest_TwoPeers_ShouldAlwaysConverge()
    {
        int iterations = 10_000; // 10 thousand random scenarios!
        
        for (int i = 0; i < iterations; i++)
        {
            // 1. Generate a random base document
            var baseText = GenerateRandomString(_random.Next(5, 50));
            var baseDoc = new TextDocument(baseText);

            // 2. Generate two concurrent random (but valid) operations
            var opA = GenerateRandomOp(baseText.Length);
            var opB = GenerateRandomOp(baseText.Length);

            // 3. Apply locally (what Alice and Bob see on their screens)
            var stateAlice = _engine.Apply(baseDoc, opA);
            var stateBob = _engine.Apply(baseDoc, opB);

            // 4. Cross-transformation (what the server/client does when receiving ops)
            var opA_prime = _engine.Transform(opA, opB, TransformPriority.IncomingWins);
            var opB_prime = _engine.Transform(opB, opA, TransformPriority.ExistingWins);

            // If transformation returns null, it means the op was canceled (e.g., deleting something already deleted).
            // Handle it as a NoOp.
            opA_prime ??= TextOp.Create(); 
            opB_prime ??= TextOp.Create();

            // 5. Apply transformed operations to cross states
            var finalAlice = _engine.Apply(stateAlice, opB_prime);
            var finalBob = _engine.Apply(stateBob, opA_prime);

            // 6. THE MOMENT OF TRUTH
            finalAlice.Content.Should().Be(finalBob.Content, 
                $"Failure in iteration {i}.\nBase: '{baseText}'\nOpA: {opA}\nOpB: {opB}");
        }
    }

    private string GenerateRandomString(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 ";
        var sb = new StringBuilder(length);
        for (int i = 0; i < length; i++) sb.Append(chars[_random.Next(chars.Length)]);
        return sb.ToString();
    }

    private TextOp GenerateRandomOp(int documentLength)
    {
        var components = new List<TextOpComponent>();
        int currentIndex = 0;

        while (currentIndex < documentLength)
        {
            int remaining = documentLength - currentIndex;
            int action = _random.Next(3); // 0: Retain, 1: Insert, 2: Delete

            if (action == 0) // Retain
            {
                int count = _random.Next(1, remaining + 1);
                components.Add(new Retain(count));
                currentIndex += count;
            }
            else if (action == 1) // Insert
            {
                string textToInsert = GenerateRandomString(_random.Next(1, 5));
                components.Add(new Insert(textToInsert));
                // Insert DOES NOT advance the original document's currentIndex
            }
            else // Delete
            {
                int count = _random.Next(1, remaining + 1);
                components.Add(new Delete(count));
                currentIndex += count;
            }
        }
        
        // We can add Inserts at the end of the document
        if (_random.NextDouble() > 0.5)
        {
            components.Add(new Insert(GenerateRandomString(_random.Next(1, 5))));
        }

        // OT requires compaction (e.g., Retain(2) + Retain(3) should be Retain(5))
        return Compact(new TextOp(components));
    }

    private TextOp Compact(TextOp op)
    {
        var compacted = new List<TextOpComponent>();
        foreach (var comp in op.Components)
        {
            if (compacted.Count == 0)
            {
                compacted.Add(comp);
                continue;
            }

            var last = compacted[^1];
            if (last is Retain r1 && comp is Retain r2)
            {
                compacted[^1] = new Retain(r1.Count + r2.Count);
            }
            else if (last is Insert i1 && comp is Insert i2)
            {
                compacted[^1] = new Insert(i1.Text + i2.Text);
            }
            else if (last is Delete d1 && comp is Delete d2)
            {
                compacted[^1] = new Delete(d1.Count + d2.Count);
            }
            else
            {
                compacted.Add(comp);
            }
        }
        return new TextOp(compacted);
    }
}
