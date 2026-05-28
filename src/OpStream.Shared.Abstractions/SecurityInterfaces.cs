namespace OpStream.Shared.Abstractions
{
    /// <summary>
    /// Bridge between the host application's identity system and OpStream.
    /// </summary>
    public interface IDocumentAuthorizer
    {
        /// <summary>
        /// Evaluates whether a user has access to a document and with what privileges.
        /// </summary>
        ValueTask<DocumentAccess> AuthorizeAsync(string documentId, CancellationToken ct = default);
    }


    public record OpValidationContext<TOp>(
        string DocumentId,
        TOp Operation);

    /// <summary>
    /// Allows injecting custom business rules before an operation is accepted.
    /// </summary>
    public interface IOpValidator<TOp>
    {
        /// <summary>
        /// Validates the operation. Return false to reject the operation.
        /// </summary>
        ValueTask<bool> ValidateAsync(OpValidationContext<TOp> context, CancellationToken ct = default);
    }

}
