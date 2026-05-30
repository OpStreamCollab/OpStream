using System.Text;

namespace OpStream.Server.Validation;

/// <summary>
/// Baseline structural validator, always registered by <c>AddOpStream</c> and run before any
/// host-supplied validators. It enforces transport-agnostic sanity limits on the weakly-typed parts
/// of every message: an attributable peer, a present and sane document id, bounded op payloads,
/// bounded awareness JSON, and bounded comment bodies.
/// <para>
/// It deliberately makes <b>no</b> authorization decisions — who may read/write/comment is the job
/// of <see cref="OpStream.Shared.Abstractions.IDocumentAuthorizer"/>. This hook only guards against
/// structurally invalid or abusive input on the intentionally-unauthenticated endpoints.
/// </para>
/// </summary>
internal sealed class DefaultInboundMessageValidator(InboundValidationOptions options) : IInboundMessageValidator
{
    public ValueTask<InboundValidationResult> ValidateAsync(InboundMessage message, CancellationToken ct = default)
        => new(Validate(message));

    private InboundValidationResult Validate(InboundMessage message)
    {
        // Reads (comment listing) may be unattributed; everything that mutates must carry a peer.
        if (message.Kind != InboundMessageKind.CommentList && string.IsNullOrWhiteSpace(message.PeerId))
            return InboundValidationResult.Invalid("Missing peer id.");

        // Every message targets a document.
        if (string.IsNullOrWhiteSpace(message.DocumentId))
            return InboundValidationResult.Invalid("Missing document id.");
        if (message.DocumentId.Length > options.MaxDocumentIdLength)
            return InboundValidationResult.Invalid($"Document id exceeds {options.MaxDocumentIdLength} characters.");
        if (ContainsControlChars(message.DocumentId))
            return InboundValidationResult.Invalid("Document id contains control characters.");

        return message.Kind switch
        {
            InboundMessageKind.Join => ValidateJoin(message),
            InboundMessageKind.Op => ValidateOp(message),
            InboundMessageKind.Awareness => ValidateAwareness(message),
            InboundMessageKind.CommentCreate or InboundMessageKind.CommentEdit => ValidateCommentBody(message),
            _ => InboundValidationResult.Valid
        };
    }

    private InboundValidationResult ValidateJoin(InboundMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.DocumentType))
            return InboundValidationResult.Invalid("Join requires a document type.");
        if (message.DocumentType!.Length > options.MaxDocumentTypeLength)
            return InboundValidationResult.Invalid($"Document type exceeds {options.MaxDocumentTypeLength} characters.");
        return InboundValidationResult.Valid;
    }

    private InboundValidationResult ValidateOp(InboundMessage message)
    {
        if (message.Payload.IsEmpty)
            return InboundValidationResult.Invalid("Operation payload is empty.");
        if (message.Payload.Length > options.MaxOpPayloadBytes)
            return InboundValidationResult.Invalid($"Operation payload exceeds {options.MaxOpPayloadBytes} bytes.");
        if (message.BaseRevision is < 0)
            return InboundValidationResult.Invalid("Base revision must be non-negative.");
        return InboundValidationResult.Valid;
    }

    private InboundValidationResult ValidateAwareness(InboundMessage message)
    {
        // A cleared/absent presence is acceptable.
        if (message.Data is null)
            return InboundValidationResult.Valid;

        var byteCount = Encoding.UTF8.GetByteCount(message.Data.Value.GetRawText());
        if (byteCount > options.MaxAwarenessBytes)
            return InboundValidationResult.Invalid($"Awareness payload exceeds {options.MaxAwarenessBytes} bytes.");
        return InboundValidationResult.Valid;
    }

    private InboundValidationResult ValidateCommentBody(InboundMessage message)
    {
        if (message.Text is null)
            return InboundValidationResult.Invalid("Comment body is required.");
        if (message.Text.Length > options.MaxCommentBodyLength)
            return InboundValidationResult.Invalid($"Comment body exceeds {options.MaxCommentBodyLength} characters.");
        return InboundValidationResult.Valid;
    }

    private static bool ContainsControlChars(string value)
    {
        foreach (var c in value)
        {
            // Allow nothing in the C0/C1 control ranges — ids are opaque keys, never multi-line text.
            if (char.IsControl(c))
                return true;
        }
        return false;
    }
}
