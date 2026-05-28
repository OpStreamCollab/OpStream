using OpStream.Shared.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpStream.Server.Multitenancy
{
    /// <summary>
    /// Default implementation of <see cref="ITenantProvider"/> that returns an empty tenant ID.
    /// Used when multi-tenancy is not explicitly configured.
    /// </summary>
    internal class DefaultTenantProvider : ITenantProvider
    {
        /// <inheritdoc/>
        public string GetCurrentTenantId() => string.Empty;
    }
}
