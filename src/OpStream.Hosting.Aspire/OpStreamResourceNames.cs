namespace OpStream.Hosting.Aspire;

/// <summary>
/// Canonical resource names that AppHost extensions emit and consumer projects
/// read at startup. Keeping them in one place avoids "magic string" drift between
/// the AppHost project and the OpStream-enabled service projects.
/// </summary>
public static class OpStreamResourceNames
{
    /// <summary>
    /// Default Aspire resource name for the Redis instance used by OpStream
    /// (both as storage and as backplane).
    /// </summary>
    public const string Redis = "opstream-redis";

    /// <summary>
    /// Default Aspire resource name for the relational database used by OpStream
    /// (cold storage and history).
    /// </summary>
    public const string RelationalDatabase = "opstream-db";

    /// <summary>
    /// Configuration key consumed by OpStream-enabled services to discover
    /// the Redis connection string.
    /// </summary>
    public const string RedisConnectionStringKey = "ConnectionStrings:" + Redis;

    /// <summary>
    /// Configuration key consumed by OpStream-enabled services to discover
    /// the database connection string.
    /// </summary>
    public const string RelationalConnectionStringKey = "ConnectionStrings:" + RelationalDatabase;
}
