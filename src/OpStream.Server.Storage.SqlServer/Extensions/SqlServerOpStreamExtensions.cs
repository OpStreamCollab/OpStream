using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpStream.Server.Storage;
using OpStream.Server.Storage.EntityFrameworkCore;
using OpStream.Server.Storage.SqlServer;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for configuring SQL Server storage.
/// </summary>
public static class SqlServerOpStreamExtensions
{
    /// <summary>
    /// Replaces the current storage with SQL Server via Entity Framework Core.
    /// <para>
    /// This package ships pre-built migrations for SQL Server. If you need to customise
    /// the schema, inherit from <see cref="SqlServerOpStreamDbContext"/> and point
    /// <c>MigrationsAssembly</c> at your own assembly.
    /// </para>
    /// </summary>
    public static IOpStreamBuilder UseSqlServerStorage(
        this IOpStreamBuilder builder,
        string connectionString)
    {
        builder.Services.AddDbContextFactory<SqlServerOpStreamDbContext>(options =>
            options.UseSqlServer(
                connectionString,
                b => b.MigrationsAssembly(typeof(SqlServerOpStreamDbContext).Assembly.FullName)));

        return builder.UseEfCoreStorage<SqlServerOpStreamDbContext>();
    }


}
