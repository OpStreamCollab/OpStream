using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;
using OpStream.Server.Models;

namespace OpStream.Server.Storage;

/// <summary>
/// A hosted service that automatically applies database migrations on startup if enabled in options.
/// </summary>
internal class MigrationHostedService(
    IServiceProvider serviceProvider,
    OpStreamOptions options,
    ILogger<MigrationHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.AutomaticMigrationsEnabled)
        {
            logger.LogInformation("Automatic migrations are disabled.");
            return;
        }

        logger.LogInformation("Applying automatic migrations...");
        
        // MigrationApplicator is registered as a singleton, so we can get it directly or via scope.
        // Using a scope is generally safer for startup tasks in case any migrator is scoped.
        using var scope = serviceProvider.CreateScope();
        var applicator = scope.ServiceProvider.GetRequiredService<MigrationApplicator>();
        await applicator.ApplyMigrationsAsync(cancellationToken);
        
        logger.LogInformation("Automatic migrations applied successfully.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
