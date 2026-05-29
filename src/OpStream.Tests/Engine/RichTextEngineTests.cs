using FluentAssertions;
using OpStream.Server.Engine.RichText;
using OpStream.Shared.Messages;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace OpStream.Tests.Engine
{

    public class RichTextEngineTests
    {
        [Fact]
        public void Invert_Should_RestoreFormatAndText()
        {
            var engine = new RichTextEngine();

            // 1. Estado original: "Hola" en negrita
            var originalAttrs = new TextAttributes { ["bold"] = true };
            var baseDoc = new RichTextDocument(new[] { new Insert("Hola", originalAttrs) });

            // 2. Operación a realizar:
            // - Quitar la negrita a los 2 primeros caracteres (Retain 2 con bold: null)
            // - Borrar los últimos 2 caracteres (Delete 2)
            // - Añadir " Mundo" en color rojo
            var op = RichTextOp.Create(
                new Retain(2, new TextAttributes { ["bold"] = null }),
                new Delete(2),
                new Insert(" Mundo", new TextAttributes { ["color"] = "red" })
            );

            // 3. Generamos la Inversa
            var invertedOp = engine.Invert(op, baseDoc);

            // La Inversa debería ser capaz de deshacer todo este desastre.

            // 4. EL TEST DEL VIAJE EN EL TIEMPO
            var state2 = engine.Apply(baseDoc, op);
            var restoredState = engine.Apply(state2, invertedOp);

            // Al aplicarla, deberíamos volver exactamente al estado "Hola" con negrita.
            restoredState.Content.Should().HaveCount(1);
            restoredState.Content[0].Text.Should().Be("Hola");

            // Cuidado con la aserción de diccionarios, mejor comprobar que contiene la key
            restoredState.Content[0].Attributes.Should().NotBeNull();
            restoredState.Content[0].Attributes!.ContainsKey("bold").Should().BeTrue();
            restoredState.Content[0].Attributes!["bold"]!.ToString().Should().Be("True");
        }

        [Fact]
        public void Transform_OperationsWithDifferentImplicitRetainLengths_ShouldConverge()
        {
            var engine = new RichTextEngine();
            var baseDoc = new RichTextDocument(new[] { new Insert("Hello World") });

            // Alice applies bold to "Hello" (implicitly retains the rest)
            var opAlice = RichTextOp.Create(new Retain(5, new TextAttributes { ["bold"] = true }));
            
            // Bob applies italic to the whole "Hello World"
            var opBob = RichTextOp.Create(new Retain(11, new TextAttributes { ["italic"] = true }));

            // OT principles dictate that missing trailing retains are implicit.
            // This test will FAIL because Transform throws an InvalidOperationException 
            // ("Incoming operation is shorter than existing." or vice versa).
            var transformedAlice = engine.Transform(opAlice, opBob, TransformPriority.IncomingWins);

            transformedAlice.Should().NotBeNull();
        }

        [Fact]
        public void Compose_OpsWithoutTrailingRetains_ShouldNotLoseOperations()
        {
            var engine = new RichTextEngine();
            var doc = new RichTextDocument(Array.Empty<Insert>());

            // opA: Insert "A" 
            // opB: Insert "B" 
            var opA = RichTextOp.Create(new Insert("A", null));
            var opB = RichTextOp.Create(new Insert("B", null));

            var composed = engine.Compose(opA, opB);

            var state1 = engine.Apply(doc, opA);
            var state2 = engine.Apply(state1, opB); // Expected: "BA"

            var composedState = engine.Apply(doc, composed!);

            // This will FAIL because the Compose method breaks early when iterB is empty,
            // dropping the remaining Insert("A") from opA.
            composedState.Content.Should().HaveCount(1);
            composedState.Content[0].Text.Should().Be("BA");
        }

        [Fact]
        public void Invert_OutOfBoundsRetainWithAttributes_ShouldKeepConsistentLength()
        {
            var engine = new RichTextEngine();
            var baseDoc = new RichTextDocument(new[] { new Insert("Hello") });

            // Bug: Retain with attributes on out-of-bounds truncates the inverted component length,
            // but Retain WITHOUT attributes preserves the full length. This leads to length mismatch
            // between the original operation and its inverse, causing OT desync.
            var opWithAttrs = RichTextOp.Create(new Retain(10, new TextAttributes { ["bold"] = true }));
            var opWithoutAttrs = RichTextOp.Create(new Retain(10));

            var invertedWithAttrs = engine.Invert(opWithAttrs, baseDoc);
            var invertedWithoutAttrs = engine.Invert(opWithoutAttrs, baseDoc);

            var invertedWithAttrsLength = invertedWithAttrs.Components.Sum(c => c is Retain r ? r.Count : 0);
            var invertedWithoutAttrsLength = invertedWithoutAttrs.Components.Sum(c => c is Retain r ? r.Count : 0);

            // This will FAIL because invertedWithAttrsLength will be 5, but invertedWithoutAttrsLength will be 10.
            invertedWithAttrsLength.Should().Be(invertedWithoutAttrsLength, "Invert should produce operations of identical operational length regardless of attributes presence");
        }
    }
}
