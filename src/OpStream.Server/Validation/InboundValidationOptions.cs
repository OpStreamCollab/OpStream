namespace OpStream.Server.Validation;

/// <summary>
/// Tunable limits enforced by <see cref="DefaultInboundMessageValidator"/>. Configure via
/// <c>AddOpStream(o =&gt; o.Validation. ... )</c>. Every limit is a hard ceiling — messages that
/// exceed it are rejected before the server processes them.
/// </summary>
public sealed class InboundValidationOptions
{
    /// <summary>Maximum length (in characters) of a document id. Default 256.</summary>
    public int MaxDocumentIdLength { get; set; } = 256;

    /// <summary>Maximum length (in characters) of a document type discriminator. Default 128.</summary>
    public int MaxDocumentTypeLength { get; set; } = 128;

    /// <summary>Maximum size (in bytes) of an operation payload. Default 1 MiB.</summary>
    public int MaxOpPayloadBytes { get; set; } = 1 * 1024 * 1024;

    /// <summary>Maximum size (in bytes, UTF-8) of an awareness/presence JSON blob. Default 64 KiB.</summary>
    public int MaxAwarenessBytes { get; set; } = 64 * 1024;

    /// <summary>Maximum length (in characters) of a comment body. Default 16 KiB.</summary>
    public int MaxCommentBodyLength { get; set; } = 16 * 1024;
}
