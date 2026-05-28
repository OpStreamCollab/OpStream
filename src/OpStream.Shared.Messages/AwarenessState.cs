using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpStream.Shared.Messages
{
    /// <summary>
    /// Represents the ephemeral presence of a user (cursor, name, etc.).
    /// </summary>
    public record AwarenessState(
        string PeerId,
        JsonElement Data,
        DateTimeOffset LastUpdated);

    
}
