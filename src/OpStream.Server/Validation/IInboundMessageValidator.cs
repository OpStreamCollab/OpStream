namespace OpStream.Server.Validation;

/// <summary>
/// A hook invoked for every inbound client message, on every transport, before the server acts on
/// it. Implement this to screen untrusted input — reject malformed, oversized, or otherwise
/// unacceptable traffic.
/// <para>
/// The transport endpoints are intentionally unauthenticated and most messages are only weakly
/// typed (opaque op payloads, arbitrary awareness JSON), so this is the single choke point where
/// such input can be validated centrally rather than per-transport.
/// </para>
/// <para>
/// Multiple validators may be registered via <c>AddInboundMessageValidator&lt;T&gt;()</c>; they run
/// in registration order (after the built-in <see cref="DefaultInboundMessageValidator"/>) and the
/// first rejection short-circuits the rest. Validators must be side-effect free and fast — they sit
/// on the hot path of every message.
/// </para>
/// </summary>
public interface IInboundMessageValidator
{
    /// <summary>Validates a single inbound message.</summary>
    ValueTask<InboundValidationResult> ValidateAsync(InboundMessage message, CancellationToken ct = default);
}

/// <summary>
/// Runs the registered <see cref="IInboundMessageValidator"/> chain for an inbound message. The
/// router facades depend on this rather than the raw validator collection so the fan-out logic
/// lives in one place.
/// </summary>
public interface IInboundMessageValidationPipeline
{
    /// <summary>Runs every registered validator, returning the first rejection or <see cref="InboundValidationResult.Valid"/>.</summary>
    ValueTask<InboundValidationResult> ValidateAsync(InboundMessage message, CancellationToken ct = default);
}
