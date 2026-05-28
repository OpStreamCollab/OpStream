using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpStream.Shared.Abstractions
{
    /// <summary>
    /// Allows the Host system to inject the initial state of a document 
    /// when it is opened for the first time and no Snapshot exists in the Store.
    /// </summary>
    public interface IDocumentSeeder<TDoc>
    {
        /// <summary>
        /// Returns the initial document, or null if the document does not exist 
        /// (to reject creation).
        /// </summary>
        /// <param name="documentId">The unique identifier of the document.</param>
        /// <param name="ct">The cancellation token.</param>
        /// <returns>The initial state of the document, or null if not found.</returns>
        ValueTask<TDoc?> GetInitialStateAsync(string documentId, CancellationToken ct = default);
    }
}
