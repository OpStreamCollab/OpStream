using FluentAssertions;
using OpStream.Server.Engine.Text;
using OpStream.Server.Models;
using Xunit;

namespace OpStream.Tests.Engine;

/// <summary>
/// Unit tests for the TextOtEngine class.
/// </summary>
public class TextOtEngineTests
{
    private readonly TextOtEngine _engine = new();

    /// <summary>
    /// Verifies that the Apply method correctly inserts text into a document.
    /// </summary>
    [Fact]
    public void Apply_Should_InsertTextCorrectly()
    {
        var doc = new TextDocument("Hello");
        var op = TextOp.Create(new Retain(5), new Insert(" World"));
        
        var result = _engine.Apply(doc, op);
        
        result.Content.Should().Be("Hello World");
    }

    /// <summary>
    /// Verifies that concurrent inserts converge correctly after transformation.
    /// </summary>
    [Fact]
    public void Transform_ConcurrentInserts_ShouldConverge()
    {
        // Base state: "A"

        // Alice tries to insert "B" at the end: "AB"
        var opAlice = TextOp.Create(new Retain(1), new Insert("B")); 

        // Bob tries to insert "C" at the beginning: "CA"
        var opBob = TextOp.Create(new Insert("C"), new Retain(1));

        // Transform Alice assuming Bob's op arrived first at the server
        var transformedAlice = _engine.Transform(opAlice, opBob, TransformPriority.IncomingWins);

        // Alice's transformed operation should now skip Bob's "C"
        // Expected result: Retain(2), Insert("B") -> "CAB"

        transformedAlice.Should().NotBeNull();
        transformedAlice!.Components[0].Should().BeOfType<Retain>()
            .Which.Count.Should().Be(2); 
    }

    [Fact]
    public void Compose_SequentialOps_ShouldProduceCombinedResult()
    {
        // State: "Hello"
        // Op1: Insert " World" -> "Hello World"
        var op1 = TextOp.Create(new Retain(5), new Insert(" World"));
        // Op2: Delete " World" and Insert "!" -> "Hello!"
        var op2 = TextOp.Create(new Retain(5), new Delete(6), new Insert("!"));

        var composed = _engine.Compose(op1, op2);

        composed.Should().NotBeNull();
        // Result should be: Retain(5), Insert("!")
        var doc = new TextDocument("Hello");
        var result = _engine.Apply(doc, composed!);
        result.Content.Should().Be("Hello!");
    }

    [Fact]
    public void Invert_Op_ShouldRevertChanges()
    {
        var baseDoc = new TextDocument("Hello");
        var op = TextOp.Create(new Retain(5), new Insert(" World"));
        var newState = _engine.Apply(baseDoc, op);

        var inverted = _engine.Invert(op, baseDoc);
        var revertedState = _engine.Apply(newState, inverted);

        revertedState.Content.Should().Be(baseDoc.Content);
    }

    [Fact]
    public void Invert_Should_ReturnExactOppositeOperation()
    {
        var engine = new TextOtEngine();

        // Estado original: "Hola Mundo"
        var baseDoc = new TextDocument("Hola Mundo");

        // Operación: Cambiar "Hola Mundo" a "Hola Amigo"
        // Retenemos "Hola ", Borramos "Mundo", Insertamos "Amigo"
        var op = TextOp.Create(
            new Retain(5),
            new Delete(5),
            new Insert("Amigo")
        );

        // Generamos la inversa
        var invertedOp = engine.Invert(op, baseDoc);

        // La operación inversa debería ser: 
        // Retener 5, Insertar "Mundo" (lo que borramos), Borrar 5 (lo que insertamos)
        invertedOp.Components.Should().HaveCount(3);

        invertedOp.Components[0].Should().BeOfType<Retain>().Which.Count.Should().Be(5);

        // Las inserciones y borrados pueden invertir su orden dependiendo del algoritmo de compactación,
        // pero conceptualmente deben hacer el trabajo.
        invertedOp.Components.Should().ContainSingle(c => c is Insert && ((Insert)c).Text == "Mundo");
        invertedOp.Components.Should().ContainSingle(c => c is Delete && ((Delete)c).Count == 5);

        // LA PRUEBA DEFINITIVA: S(n+1) = Apply(S, Op) ---> Apply(S(n+1), Invert(Op)) == S
        var state2 = engine.Apply(baseDoc, op);
        var restoredState = engine.Apply(state2, invertedOp);

        restoredState.Content.Should().Be("Hola Mundo");
    }

    [Fact]
    public void Compose_OpsWithoutTrailingRetains_ShouldNotLoseOperations()
    {
        // Bug: If opB has no more components, the Compose loop breaks and drops any remaining components in opA.
        // Document: ""
        // opA: Insert "A" 
        // opB: Insert "B" 
        var opA = TextOp.Create(new Insert("A"));
        var opB = TextOp.Create(new Insert("B"));

        var composed = _engine.Compose(opA, opB);

        var doc = new TextDocument("");
        var state1 = _engine.Apply(doc, opA);
        var state2 = _engine.Apply(state1, opB); // Expected: "BA"

        var composedState = _engine.Apply(doc, composed!);

        // This will FAIL because composed is missing Insert("A")
        composedState.Content.Should().Be(state2.Content);
    }

    [Fact]
    public void Apply_OutOfBoundsDeleteAndRetain_ShouldNotCrash()
    {
        // Bug: If a delete pushes the index out of bounds, a subsequent retain will throw ArgumentOutOfRangeException.
        var doc = new TextDocument("Hello");
        var op = TextOp.Create(new Delete(10), new Retain(5));

        // This will CRASH with ArgumentOutOfRangeException
        var result = _engine.Apply(doc, op);

        result.Content.Should().Be("");
    }

    [Fact]
    public void Invert_OutOfBoundsDelete_ShouldNotCrash()
    {
        // Bug: If a delete exceeds the document length, Invert's Substring will throw ArgumentOutOfRangeException.
        var doc = new TextDocument("Hello");
        var op = TextOp.Create(new Delete(10));

        // This will CRASH with ArgumentOutOfRangeException
        var inverted = _engine.Invert(op, doc);

        inverted.Components.Should().HaveCount(1);
        inverted.Components[0].Should().BeOfType<Insert>()
            .Which.Text.Should().Be("Hello"); // Should gracefully cap at existing text
    }
}
