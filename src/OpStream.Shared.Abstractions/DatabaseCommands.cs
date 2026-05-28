using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpStream.Shared.Abstractions
{
    /// <summary>
    /// Discriminator for management commands evaluated by <see cref="IDatabaseCommandAuthorizer"/>.
    /// </summary>
    public enum DatabaseCommandType
    {
        ListDocuments,
        GetDocumentInfo,
        GetSnapshot,
        DeleteDocument,
        CompactDocument,
        ListMilestones,
        PurgeHistory,
        PurgeTenant
    }

    /// <summary>
    /// Carries the data the host needs to decide whether a management command is allowed.
    /// <para>
    /// <see cref="DocumentId"/> is the <em>local</em> id supplied by the caller (the same shape the host
    /// already authorizes via <see cref="IDocumentAuthorizer"/>). Tenant scoping is implicit through
    /// <see cref="ITenantProvider"/> — the host typically does not need to read it from <see cref="Args"/>.
    /// </para>
    /// </summary>
    public record DatabaseCommandContext(
        DatabaseCommandType Command,
        string? DocumentId,
        IReadOnlyDictionary<string, string>? Args = null);

    /// <summary>
    /// Bridge between the host application's identity system and OpStream's management surface.
    /// One authorizer covers every <see cref="DatabaseCommandType"/>; the host decides per command.
    /// </summary>
    public interface IDatabaseCommandAuthorizer
    {
        /// <summary>
        /// Returns <c>true</c> when the current caller is allowed to execute the specified command.
        /// </summary>
        ValueTask<bool> AuthorizeAsync(DatabaseCommandContext ctx, CancellationToken ct = default);
    }
}
