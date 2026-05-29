using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpStream.Server.Comments;
using OpStream.Server.Storage;
using OpStream.Server.Storage.EntityFrameworkCore;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for configuring Entity Framework Core storage.
/// </summary>
public static class OpStreamEfCoreExtensions
{
    /// <summary>
    /// Replaces the current storage with <see cref="EfCoreDocumentStore{TContext}"/>.
    /// Works over any EF Core provider (SQL Server, PostgreSQL, SQLite, MySQL…).
    /// </summary>
    /// <typeparam name="TContext">
    /// The <see cref="DbContext"/> subclass configured with OpStream's entity model.
    /// </typeparam>
    public static IOpStreamBuilder UseEfCoreStorage<TContext>(this IOpStreamBuilder builder)
        where TContext : DbContext
    {
        builder.Services.TryAddSingleton<EfCoreDocumentStore<TContext>>();

        // Replace the defaults registered by AddOpStream().
        builder.Services.Replace(ServiceDescriptor.Singleton<IDocumentStore>(
            sp => sp.GetRequiredService<EfCoreDocumentStore<TContext>>()));
        builder.Services.Replace(ServiceDescriptor.Singleton<IHistoryStore>(
            sp => sp.GetRequiredService<EfCoreDocumentStore<TContext>>()));

        // Register the migrator for this context.
        builder.Services.AddSingleton<IStorageMigrator, EfCoreStorageMigrator<TContext>>();

        return builder;
    }

    /// <summary>
    /// Replaces the default <see cref="ICommentStore"/> with <see cref="EfCoreCommentStore{TContext}"/>,
    /// backed by the same <typeparamref name="TContext"/> already configured via
    /// <see cref="UseEfCoreStorage{TContext}"/>.
    /// </summary>
    /// <remarks>
    /// Call this <em>after</em> <c>UseEfCoreStorage&lt;TContext&gt;()</c> so the
    /// <c>IDbContextFactory&lt;TContext&gt;</c> is already registered.
    /// </remarks>
    public static IOpStreamBuilder UseEfCoreCommentStorage<TContext>(this IOpStreamBuilder builder)
        where TContext : OpStreamDbContext
    {
        builder.Services.TryAddSingleton<EfCoreCommentStore<TContext>>();
        builder.Services.Replace(ServiceDescriptor.Singleton<ICommentStore>(
            sp => sp.GetRequiredService<EfCoreCommentStore<TContext>>()));
        return builder;
    }
}
