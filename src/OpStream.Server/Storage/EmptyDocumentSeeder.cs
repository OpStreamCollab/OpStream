using OpStream.Shared.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpStream.Server.Storage
{
    /// <summary>
    /// Default implementation of <see cref="IDocumentSeeder{TDoc}"/> that creates empty documents.
    /// </summary>
    /// <typeparam name="TDoc">The type of the document state.</typeparam>
    internal class EmptyDocumentSeeder<TDoc> : IDocumentSeeder<TDoc>
    {
        /// <inheritdoc/>
        public ValueTask<TDoc?> GetInitialStateAsync(string documentId, CancellationToken ct)
        {
            object? instance = typeof(TDoc) switch
            {
                Type t when t == typeof(Engine.Text.TextDocument) => new Engine.Text.TextDocument(""),
                Type t when t == typeof(Engine.RichText.RichTextDocument) => new Engine.RichText.RichTextDocument(),
                Type t when t == typeof(Engine.Json.Json_Document) => new Engine.Json.Json_Document(),
                Type t when t == typeof(Engine.Form.FormDocument) => new Engine.Form.FormDocument(),
                Type t when t == typeof(Engine.Table.TableDocument) => new Engine.Table.TableDocument(),
                Type t when t == typeof(Engine.Tree.TreeDocument) => new Engine.Tree.TreeDocument(),
                _ => null
            };

            if (instance != null)
            {
                return ValueTask.FromResult<TDoc?>((TDoc)instance);
            }

            return ValueTask.FromResult(default(TDoc));
        }
    }
}
