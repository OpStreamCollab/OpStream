using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using OpStream.Server.Comments;
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

    /// <summary>
    /// Replaces the default <see cref="ICommentStore"/> with <see cref="MongoCommentStore"/>,
    /// backed by the same MongoDB database already configured via
    /// <see cref="UseMongoDbStorage"/>.
    /// </summary>
    /// <remarks>
    /// Call this <em>after</em> <c>UseMongoDbStorage()</c> so the
    /// <c>IMongoDatabase</c> is already registered.
    /// Atomicity note: without a replica set MongoDB cannot do multi-document transactions;
    /// anchor recovery falls back to the <c>RehydrateOpAsync</c> replay path.
    /// </remarks>
    public static IOpStreamBuilder UseMongoDbCommentStorage(this IOpStreamBuilder builder)
    {
        builder.Services.TryAddSingleton<MongoCommentStore>();
        builder.Services.Replace(ServiceDescriptor.Singleton<ICommentStore>(
            sp => sp.GetRequiredService<MongoCommentStore>()));
        return builder;
    }
}
