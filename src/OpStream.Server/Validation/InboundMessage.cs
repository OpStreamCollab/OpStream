using System.Text.Json;

namespace OpStream.Server.Validation;

/// <summary>
/// Discriminates the kind of inbound client message handed to the validation hook.
/// </summary>
public enum InboundMessageKind
{
    /// <summary>A peer opening a document (the handshake).</summary>
    Join,
    /// <summary>A peer submitting an operation against a document.</summary>
    Op,
    /// <summary>A peer updating its awareness/presence state.</summary>
    Awareness,
    /// <summary>A peer creating a root comment or reply.</summary>
    CommentCreate,
    /// <summary>A peer editing the body of an existing comment.</summary>
    CommentEdit,
    /// <summary>A peer resolving a comment.</summary>
    CommentResolve,
    /// <summary>A peer deleting a comment.</summary>
    CommentDelete,
    /// <summary>A peer listing the open comments of a document.</summary>
    CommentList
}

/// <summary>
/// A transport-agnostic view of a single inbound client message, handed to every
/// <see cref="IInboundMessageValidator"/> before the server acts on it.
/// <para>
/// The three transport endpoints (WebSocket, SignalR, gRPC) are intentionally unauthenticated and
/// most of their traffic is only weakly typed — opaque operation payloads and arbitrary awareness
/// JSON. All of that traffic funnels through the router facades in this library, which build one of
/// these descriptors and run it through the validation pipeline. Fields that are not relevant to a
/// given <see cref="Kind"/> are left at their defaults (<c>null</c> / empty).
/// </para>
/// </summary>
/// <param name="Kind">The kind of message.</param>
/// <param name="PeerId">The connection/peer the message arrived on. Empty for unattributed reads.</param>
/// <param name="DocumentId">The (local) document the message targets.</param>
/// <param name="DocumentType">The document type discriminator (Join only).</param>
/// <param name="ProtocolVersion">The protocol version advertised by the client (Join only).</param>
/// <param name="BaseRevision">The revision an operation is based on (Op only).</param>
/// <param name="Payload">The opaque operation payload (Op only).</param>
/// <param name="Data">Parsed awareness/presence JSON (Awareness only).</param>
/// <param name="Text">Free-form text such as a comment body (CommentCreate / CommentEdit).</param>
public sealed record InboundMessage(
    InboundMessageKind Kind,
    string PeerId,
    string? DocumentId = null,
    string? DocumentType = null,
    int? ProtocolVersion = null,
    long? BaseRevision = null,
    ReadOnlyMemory<byte> Payload = default,
    JsonElement? Data = null,
    string? Text = null);
