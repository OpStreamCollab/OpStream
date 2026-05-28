using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpStream.Shared.Abstractions
{
    /// <summary>
    /// Contract that the Host must implement to tell OpStream 
    /// which Tenant the current call is executing in.
    /// </summary>
    public interface ITenantProvider
    {
        /// <summary>
        /// Returns the current tenant ID.
        /// </summary>
        /// <returns>The ID of the current tenant.</returns>
        string GetCurrentTenantId();
    }
}
