using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpStream.Server.Session
{
    /// <summary>
    /// Responsible for instantiating the correct session and engine 
    /// based on the document type.
    /// </summary>
    public interface IDocumentSessionFactory
    {
        /// <summary>
        /// The type of document that this factory knows how to handle (e.g., "text", "rich-text").
        /// </summary>
        string DocumentType { get; }

        /// <summary>
        /// Creates and initializes a session with the state hydrated from the database.
        /// </summary>
        /// <param name="documentId">The ID of the document.</param>
        /// <param name="initialRevision">The initial revision number.</param>
        /// <param name="snapshotData">Optional snapshot data to initialize the state.</param>
        /// <param name="ct">The cancellation token.</param>
        /// <returns>A task representing the session creation, returning the new session.</returns>
        Task<IDocumentSession> CreateSessionAsync(
            string documentId,
            long initialRevision,
            ReadOnlyMemory<byte>? snapshotData,
            CancellationToken ct = default);
    }
}
