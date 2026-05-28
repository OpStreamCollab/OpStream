using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        return builder;
    }
}
