using System.Text.Json;

namespace OpStream.Shared.Messages
{
    /// <summary>
    /// Message sent and received over the network to update presence.
    /// </summary>
    public record AwarenessUpdate(JsonElement Data);
}
