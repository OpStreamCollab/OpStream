using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpStream.Server.Storage;
using OpStream.Server.Storage.EntityFrameworkCore;
using OpStream.Server.Storage.PostgreSQL;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for configuring PostgreSQL storage via Npgsql.
/// </summary>
public static class PostgreSqlOpStreamExtensions
{
    /// <summary>
    /// Replaces the current storage with PostgreSQL via Entity Framework Core (Npgsql).
    /// <para>
    /// This package ships pre-built migrations for PostgreSQL. If you need to customise
    /// the schema, inherit from <see cref="PostgreSqlOpStreamDbContext"/> and point
    /// <c>MigrationsAssembly</c> at your own assembly.
    /// </para>
    /// </summary>
    public static IOpStreamBuilder UsePostgreSqlStorage(
        this IOpStreamBuilder builder,
        string connectionString)
    {
        builder.Services.AddDbContextFactory<PostgreSqlOpStreamDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                b => b.MigrationsAssembly(typeof(PostgreSqlOpStreamDbContext).Assembly.FullName)));

        return builder.UseEfCoreStorage<PostgreSqlOpStreamDbContext>();
    }

  
}
