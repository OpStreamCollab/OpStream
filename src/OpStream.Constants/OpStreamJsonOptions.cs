using System.Text.Json;

namespace OpStream.Constants;

/// <summary>
/// Centralized JSON serialization settings for OpStream.
/// </summary>
public static class OpStreamJsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}
