using OpStream.Shared.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpStream.Server.Multitenancy
{
    /// <summary>
    /// Implementation of <see cref="IDocumentIdGlobalizer"/> that includes tenant information in the global ID.
    /// </summary>
    internal class TenantAwareDocumentIdGlobalizer(ITenantProvider tenantProvider) : IDocumentIdGlobalizer
    {
        // TODO: Add check that neither the tenantId nor the documentId contains the separator to avoid collisions
        private const string Separator = ":#:";

        /// <inheritdoc/>
        public string ToGlobalId(string localDocumentId)
        {
            var tenantId = tenantProvider.GetCurrentTenantId();
            return $"{tenantId}{Separator}{localDocumentId}";
        }

        /// <inheritdoc/>
        public string ToLocalId(string globalDocumentId)
        {
            var parts = globalDocumentId.Split(Separator, 2);
            return parts.Length == 2 ? parts[1] : globalDocumentId;
        }

        /// <inheritdoc/>
        public string GetCurrentTenantPrefix()
        {
            var tenantId = tenantProvider.GetCurrentTenantId();
            return $"{tenantId}{Separator}";
        }
    }
}
