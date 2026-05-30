namespace OpStream.Server.Validation;

/// <summary>
/// The outcome of validating an <see cref="InboundMessage"/>. A rejection carries a human-readable
/// <see cref="Reason"/> which transports surface back to the client as an error response.
/// </summary>
public readonly record struct InboundValidationResult(bool IsValid, string? Reason = null)
{
    /// <summary>A passing result.</summary>
    public static InboundValidationResult Valid { get; } = new(true);

    /// <summary>Creates a failing result with the given reason.</summary>
    public static InboundValidationResult Invalid(string reason) => new(false, reason);
}
