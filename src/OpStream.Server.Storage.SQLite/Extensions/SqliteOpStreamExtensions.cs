using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpStream.Server.Storage;
using OpStream.Server.Storage.EntityFrameworkCore;
using OpStream.Server.Storage.SQLite;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for configuring SQLite storage.
/// </summary>
public static class SqliteOpStreamExtensions
{
    /// <summary>
    /// Replaces the current storage with SQLite via Entity Framework Core.
    /// <para>
    /// This package ships pre-built migrations for SQLite. If you need to customise
    /// the schema, inherit from <see cref="SqliteOpStreamDbContext"/> and point
    /// <c>MigrationsAssembly</c> at your own assembly.
    /// </para>
    /// </summary>
    public static IOpStreamBuilder UseSqliteStorage(
        this IOpStreamBuilder builder,
        string connectionString)
    {
        builder.Services.AddDbContextFactory<SqliteOpStreamDbContext>(options =>
            options.UseSqlite(
                connectionString,
                b => b.MigrationsAssembly(typeof(SqliteOpStreamDbContext).Assembly.FullName)));

        builder.Services.TryAddSingleton<EfCoreDocumentStore<SqliteOpStreamDbContext>>();

        // Replace the defaults registered by AddOpStream().
        builder.Services.Replace(ServiceDescriptor.Singleton<IDocumentStore>(
            sp => sp.GetRequiredService<EfCoreDocumentStore<SqliteOpStreamDbContext>>()));
        builder.Services.Replace(ServiceDescriptor.Singleton<IHistoryStore>(
            sp => sp.GetRequiredService<EfCoreDocumentStore<SqliteOpStreamDbContext>>()));

        return builder;
    }


}
