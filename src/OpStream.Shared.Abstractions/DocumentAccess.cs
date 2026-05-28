using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpStream.Shared.Abstractions
{
    /// <summary>
    /// Defines a user's resolved permissions over a document.
    /// </summary>
    public readonly record struct DocumentAccess(
        bool CanRead,
        bool CanWrite,
        bool CanComment,
        bool CanManagePresence,
        bool CanChat,
        IReadOnlySet<string>? RestrictedRegions = null)
    {
        public static DocumentAccess Denied => default;
        public static DocumentAccess ReadOnly() => new(true, false, false, false, false);
        public static DocumentAccess ReadWrite() => new(true, true, true, true, true);
    }
}
