namespace OpStream.Server.Validation;

/// <summary>
/// Default <see cref="IInboundMessageValidationPipeline"/>: runs the registered validators in
/// registration order and short-circuits on the first rejection.
/// </summary>
internal sealed class InboundMessageValidationPipeline(IEnumerable<IInboundMessageValidator> validators)
    : IInboundMessageValidationPipeline
{
    private readonly IInboundMessageValidator[] _validators = validators.ToArray();

    public async ValueTask<InboundValidationResult> ValidateAsync(InboundMessage message, CancellationToken ct = default)
    {
        foreach (var validator in _validators)
        {
            var result = await validator.ValidateAsync(message, ct);
            if (!result.IsValid)
                return result;
        }

        return InboundValidationResult.Valid;
    }
}
