using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpStream.Server.Multitenancy
{
    /// <summary>
    /// Responsible for converting a local DocumentId (sent by the UI client) 
    /// into a unique global identifier crossing the Tenant boundary.
    /// </summary>
    public interface IDocumentIdGlobalizer
    {
        /// <summary>
        /// Converts a local document ID to a global document ID.
        /// </summary>
        /// <param name="localDocumentId">The local document identifier.</param>
        /// <returns>The global document identifier.</returns>
        string ToGlobalId(string localDocumentId);

        /// <summary>
        /// Converts a global document ID back to its local representation.
        /// </summary>
        /// <param name="globalDocumentId">The global document identifier.</param>
        /// <returns>The local document identifier.</returns>
        string ToLocalId(string globalDocumentId);

        /// <summary>
        /// Returns the prefix every global id for the current tenant starts with.
        /// Used by management endpoints to enumerate / fan-out without leaking the separator.
        /// </summary>
        string GetCurrentTenantPrefix();
    }
}
