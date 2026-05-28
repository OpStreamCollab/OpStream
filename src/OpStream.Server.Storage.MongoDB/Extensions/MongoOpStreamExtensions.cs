using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using OpStream.Server.Storage;
using OpStream.Server.Storage.MongoDB;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for configuring MongoDB storage.
/// </summary>
public static class MongoOpStreamExtensions
{
    /// <summary>
    /// Replaces the current storage with <see cref="MongoDocumentStore"/>.
    /// </summary>
    /// <param name="connectionString">MongoDB connection string.</param>
    /// <param name="databaseName">Name of the database that will hold OpStream collections.</param>
    public static IOpStreamBuilder UseMongoDbStorage(
        this IOpStreamBuilder builder,
        string connectionString,
        string databaseName)
    {
        builder.Services.TryAddSingleton<IMongoClient>(new MongoClient(connectionString));
        builder.Services.TryAddSingleton(sp =>
            sp.GetRequiredService<IMongoClient>().GetDatabase(databaseName));
        builder.Services.TryAddSingleton<MongoDocumentStore>();

        // Replace the defaults registered by AddOpStream().
        builder.Services.Replace(ServiceDescriptor.Singleton<IDocumentStore>(
            sp => sp.GetRequiredService<MongoDocumentStore>()));
        builder.Services.Replace(ServiceDescriptor.Singleton<IHistoryStore>(
            sp => sp.GetRequiredService<MongoDocumentStore>()));

        return builder;
    }

   
}
