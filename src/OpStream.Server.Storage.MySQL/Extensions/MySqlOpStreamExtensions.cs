using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpStream.Server.Storage;
using OpStream.Server.Storage.EntityFrameworkCore;
using OpStream.Server.Storage.MySQL;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for configuring MySQL / MariaDB storage via Pomelo.
/// </summary>
public static class MySqlOpStreamExtensions
{
    /// <summary>
    /// Replaces the current storage with MySQL (or MariaDB) via Entity Framework Core (Pomelo).
    /// <para>
    /// This package ships pre-built migrations for MySQL. If you need to customise
    /// the schema, inherit from <see cref="MySqlOpStreamDbContext"/> and point
    /// <c>MigrationsAssembly</c> at your own assembly.
    /// </para>
    /// </summary>
    public static IOpStreamBuilder UseMySqlStorage(
        this IOpStreamBuilder builder,
        string connectionString)
    {
        builder.Services.AddDbContextFactory<MySqlOpStreamDbContext>(options =>
            options.UseMySql(
                connectionString,
                ServerVersion.AutoDetect(connectionString),
                b => b.MigrationsAssembly(typeof(MySqlOpStreamDbContext).Assembly.FullName)));

        builder.Services.TryAddSingleton<EfCoreDocumentStore<MySqlOpStreamDbContext>>();

        // Replace the defaults registered by AddOpStream().
        builder.Services.Replace(ServiceDescriptor.Singleton<IDocumentStore>(
            sp => sp.GetRequiredService<EfCoreDocumentStore<MySqlOpStreamDbContext>>()));
        builder.Services.Replace(ServiceDescriptor.Singleton<IHistoryStore>(
            sp => sp.GetRequiredService<EfCoreDocumentStore<MySqlOpStreamDbContext>>()));

        return builder;
    }


}
