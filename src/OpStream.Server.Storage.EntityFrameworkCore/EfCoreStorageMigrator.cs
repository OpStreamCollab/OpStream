using Microsoft.EntityFrameworkCore;
using OpStream.Server.Storage;

namespace OpStream.Server.Storage.EntityFrameworkCore;

/// <summary>
/// EF Core implementation of <see cref="IStorageMigrator"/>.
/// </summary>
/// <typeparam name="TContext">The DbContext type to migrate.</typeparam>
internal class EfCoreStorageMigrator<TContext>(IDbContextFactory<TContext> factory) : IStorageMigrator
    where TContext : DbContext
{
    /// <inheritdoc/>
    public async Task MigrateAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);
    }
}
