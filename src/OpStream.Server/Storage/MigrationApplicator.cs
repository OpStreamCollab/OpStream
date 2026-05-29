using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpStream.Server.Storage
{
    /// <summary>
    /// Defines a contract for storage providers that need to perform database migrations or schema initialization.
    /// </summary>
    public interface IStorageMigrator
    {
        /// <summary>
        /// Applies any pending migrations or initializes the schema for the active storage.
        /// </summary>
        Task MigrateAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Orchestrates the application of migrations across all registered storage migrators.
    /// </summary>
    public class MigrationApplicator(IEnumerable<IStorageMigrator> migrators)
    {
        public bool Applied { get; private set; }

        /// <summary>
        /// Asynchronously applies migrations for the active storage.
        /// </summary>
        public async Task ApplyMigrationsAsync(CancellationToken ct = default)
        {
            if (Applied) return;

            foreach (var migrator in migrators)
            {
                await migrator.MigrateAsync(ct);
            }

            Applied = true;
        }

        /// <summary>
        /// Synchronously applies migrations for the active storage.
        /// </summary>
        public void ApplyMigrations()
        {
            ApplyMigrationsAsync().GetAwaiter().GetResult();
        }
    }
}
