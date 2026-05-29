using FluentAssertions;
using OpStream.Server.Engine.RichText;
using OpStream.Shared.Messages;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace OpStream.Tests.Engine
{


    public class RichTextFuzzerTests
    {
        private readonly RichTextEngine _engine = new();
        private readonly Random _random = new(Seed: 101); // Fijo para reproducibilidad

        [Fact]
        public void FuzzTest_RichText_ShouldAlwaysConverge()
        {
            int iterations = 10_000;

            for (int i = 0; i < iterations; i++)
            {
                // 1. Generamos un documento base con Inserts aleatorios
                var baseOps = new List<Insert>();
                int baseLength = _random.Next(10, 50);
                while (baseLength > 0)
                {
                    int chunkLen = Math.Min(_random.Next(1, 10), baseLength);
                    baseOps.Add(new Insert(GenerateRandomString(chunkLen), GenerateRandomAttributes()));
                    baseLength -= chunkLen;
                }
                var baseDoc = new RichTextDocument(baseOps);

                // 2. Calculamos la longitud total real
                int totalLength = baseDoc.Content.Sum(x => x.Text.Length);

                // 3. Generamos operaciones concurrentes para esa longitud
                var opA = GenerateRandomOp(totalLength);
                var opB = GenerateRandomOp(totalLength);

                // 4. Aplicamos localmente
                var stateAlice = _engine.Apply(baseDoc, opA);
                var stateBob = _engine.Apply(baseDoc, opB);

                // 5. Transformación cruzada
                var opA_prime = _engine.Transform(opA, opB, TransformPriority.IncomingWins) ?? RichTextOp.Create();
                var opB_prime = _engine.Transform(opB, opA, TransformPriority.ExistingWins) ?? RichTextOp.Create();

                // 6. Aplicamos las cruzadas
                var finalAlice = _engine.Apply(stateAlice, opB_prime);
                var finalBob = _engine.Apply(stateBob, opA_prime);

                // 7. Aserción estricta de convergencia
                // Para comparar, serializamos a JSON porque los objetos anidados (diccionarios) pueden fallar en .Equals()
                var jsonA = JsonSerializer.Serialize(finalAlice.Content);
                var jsonB = JsonSerializer.Serialize(finalBob.Content);

                jsonA.Should().Be(jsonB, $"Fallo en iteración {i}.\nOpA: {JsonSerializer.Serialize(opA)}\nOpB: {JsonSerializer.Serialize(opB)}");
            }
        }

        private string GenerateRandomString(int length)
        {
            const string chars = "ABCDEFGHIJabcdefghij 0123";
            var sb = new StringBuilder(length);
            for (int i = 0; i < length; i++) sb.Append(chars[_random.Next(chars.Length)]);
            return sb.ToString();
        }

        private TextAttributes? GenerateRandomAttributes()
        {
            if (_random.NextDouble() > 0.6) return null; // 60% probabilidad de no tener formato

            var attrs = new TextAttributes();
            if (_random.NextDouble() > 0.5) attrs["bold"] = true;
            if (_random.NextDouble() > 0.5) attrs["color"] = _random.NextDouble() > 0.5 ? "red" : "blue";

            // Simular borrado de formato
            if (_random.NextDouble() > 0.8) attrs["italic"] = null;

            return attrs.Count > 0 ? attrs : null;
        }

        private RichTextOp GenerateRandomOp(int documentLength)
        {
            var components = new List<RichTextComponent>();
            int currentIndex = 0;

            while (currentIndex < documentLength)
            {
                int remaining = documentLength - currentIndex;
                int action = _random.Next(3);

                if (action == 0) // Retain
                {
                    int count = _random.Next(1, remaining + 1);
                    components.Add(new Retain(count, GenerateRandomAttributes()));
                    currentIndex += count;
                }
                else if (action == 1) // Insert
                {
                    components.Add(new Insert(GenerateRandomString(_random.Next(1, 5)), GenerateRandomAttributes()));
                }
                else // Delete
                {
                    int count = _random.Next(1, remaining + 1);
                    components.Add(new Delete(count));
                    currentIndex += count;
                }
            }

            // Siempre podemos insertar al final
            if (_random.NextDouble() > 0.5)
            {
                components.Add(new Insert(GenerateRandomString(_random.Next(1, 5))));
            }

            return new RichTextOp(components);
        }
    }
}
