using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpStream.Server.Comments;
using OpStream.Server.Diagnostics.HealthChecks;
using OpStream.Server.Engine;
using OpStream.Server.Engine.Text;
using OpStream.Server.Models;
using OpStream.Server.Session;
using OpStream.Server.Storage;
using OpStream.Server.Multitenancy;
using OpStream.Server.Versioning;
using OpStream.Shared.Abstractions;
using OpStream.Server.Snapshots;
using OpStream.Server.Engine.RichText;
using OpStream.Server.Engine.Json;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Fluent builder returned by <see cref="OpStreamServiceCollectionExtensions.AddOpStream"/>.
/// Extension methods on this interface follow the convention:
/// <list type="bullet">
///   <item><b>Use*()</b> — configures a <em>singleton</em> subsystem (storage, backplane, authorizer).
///         Calling it twice replaces the previous registration.</item>
///   <item><b>Add*()</b> — registers one element of a <em>collection</em> (engines, transports, validators).
///         Calling it multiple times accumulates entries.</item>
/// </list>
/// </summary>
public interface IOpStreamBuilder
{
    /// <summary>Gets the underlying service collection.</summary>
    IServiceCollection Services { get; }
}

internal sealed class OpStreamBuilder(IServiceCollection services) : IOpStreamBuilder
{
    public IServiceCollection Services { get; } = services;
}

// ─── Default no-op implementations ───────────────────────────────────────────

/// <summary>
/// Fallback authorizer that grants full access to every user.
/// Replaced automatically when <see cref="OpStreamServiceCollectionExtensions.UseAuthorization{TAuthorizer}"/>
/// is called.
/// </summary>
internal sealed class AllowAllAuthorizer : IDocumentAuthorizer
{
    public ValueTask<DocumentAccess> AuthorizeAsync(string documentId, CancellationToken ct = default)
        => ValueTask.FromResult(DocumentAccess.ReadWrite());
}

/// <summary>
/// Fallback management authorizer that denies every command.
/// The host MUST replace this with a real implementation via
/// <see cref="OpStreamServiceCollectionExtensions.UseDatabaseCommandAuthorization{TAuthorizer}"/>
/// before any management endpoint will work.
/// </summary>
internal sealed class DenyAllDatabaseCommandAuthorizer : IDatabaseCommandAuthorizer
{
    public ValueTask<bool> AuthorizeAsync(DatabaseCommandContext ctx, CancellationToken ct = default)
        => ValueTask.FromResult(false);
}

// ─── Core extension methods ───────────────────────────────────────────────────

/// <summary>
/// Extension methods for setting up OpStream services in an <see cref="IServiceCollection"/>.
/// </summary>
public static class OpStreamServiceCollectionExtensions
{
    // =========================================================================
    // Entry point
    // =========================================================================

    /// <summary>
    /// Registers the OpStream server services with sensible defaults:
    /// <list type="bullet">
    ///   <item><see cref="MemoryDocumentStore"/> — <b>not for production</b>. Replace with
    ///         <c>UseRedisStorage()</c>, <c>UseEfCoreStorage()</c>, etc.</item>
    ///   <item><c>LocalBackplane</c> — single-node mode with in-process fan-out.
    ///         Replace with <c>UseRedisBackplane()</c> for multi-node deployments.</item>
    ///   <item><see cref="AllowAllAuthorizer"/> — grants full access. Replace with
    ///         <c>UseAuthorization&lt;T&gt;()</c>.</item>
    ///   <item><c>TextOtEngine</c> registered for <c>"text"</c> documents.</item>
    /// </list>
    /// </summary>
    public static IOpStreamBuilder AddOpStream(
        this IServiceCollection services,
        Action<OpStreamOptions>? configure = null)
    {
        var options = new OpStreamOptions();
        configure?.Invoke(options);

        services.TryAddSingleton<MigrationApplicator>();
        services.AddHostedService<MigrationHostedService>();

        services.TryAddSingleton(options);
        services.TryAddSingleton(options.History);

        var builder = new OpStreamBuilder(services);

        // Core infrastructure
        services.TryAddSingleton(options.Sessions);
        services.TryAddSingleton<IDocumentLockRegistry, DocumentLockRegistry>();
        services.TryAddSingleton<IDocumentSessionRegistry, DocumentSessionRegistry>();
        services.TryAddSingleton<IAwarenessSessionRegistry, AwarenessSessionRegistry>();
        services.TryAddSingleton<IPeerRegistry, PeerRegistry>();
        services.TryAddSingleton<IDocumentExecutionPipeline, DocumentExecutionPipeline>();
        services.TryAddSingleton<IDocumentDrainCoordinator, DocumentDrainCoordinator>();
        services.TryAddSingleton<IDocumentDiagnosticsService, DocumentDiagnosticsService>();
        services.TryAddSingleton<IDocumentBackplaneGateway, DocumentBackplaneGateway>();
        services.TryAddSingleton<OpStreamStartupValidator>();
        services.TryAddSingleton<DocumentRouter>();
        services.TryAddSingleton<DatabaseCommandRouter>();
        services.TryAddSingleton<CommentRouter>();
        services.AddSingleton<IBackplaneRequestExtension>(sp => sp.GetRequiredService<DatabaseCommandRouter>());
        services.AddSingleton<IBackplaneRequestExtension>(sp => sp.GetRequiredService<CommentRouter>());
        services.TryAddSingleton<IBackplane, LocalBackplane>();
        services.TryAddSingleton<IDocumentOwnershipManager, LocalDocumentOwnershipManager>();
        services.TryAddSingleton<ITimerFactory, DefaultTimerFactory>();

        // Snapshot pipeline
        services.TryAddSingleton<IOpSnapshotter, OpSnapshotter>();
        services.TryAddSingleton<IOpHistorySnapshotter>(options.History.Enabled
            ? sp => sp.GetRequiredService<IServiceProvider>().GetRequiredService<OpHistorySnapshotter>()
            : _ => new NoopHistorySnapshotter());
        services.TryAddSingleton<ISnapshotPolicy>(new HybridSnapshotPolicy(100, TimeSpan.FromMinutes(5)));

        // Default storage — MemoryDocumentStore.
        // The router will log a warning at startup if this default is still active.
        services.TryAddSingleton<MemoryDocumentStore>();
        services.TryAddSingleton<IDocumentStore>(sp => sp.GetRequiredService<MemoryDocumentStore>());
        services.TryAddSingleton<IHistoryStore>(sp => sp.GetRequiredService<MemoryDocumentStore>());

        // Default comment store — in-memory. Replace via UseEfCoreCommentStorage(),
        // UseMongoDbCommentStorage(), or UseRedisCommentStorage().
        services.TryAddSingleton<ICommentStore, MemoryCommentStore>();

        // Versioning ref store — in-memory default. Replace via UseEfCoreVersioningStorage() etc.
        services.TryAddSingleton<IDocumentRefStore, MemoryDocumentRefStore>();
        services.TryAddSingleton<MergeDriverRegistry>();
        services.TryAddSingleton<VersioningRouter>();

        // Open-generic post-apply hook that rebases comment anchors. Becomes a no-op for
        // op types that have no IAnchorEngine<TOp> registered.
        services.TryAddSingleton(typeof(IPostApplyHook<>), typeof(CommentAnchorRebaseHook<>));

        // Anchor engines (one per op type that supports anchored comments).
        services.TryAddSingleton<IAnchorEngine<TextOp>, TextAnchorEngine>();
        services.TryAddSingleton<IAnchorEngine<RichTextOp>, RichTextAnchorEngine>();
        services.TryAddSingleton<IAnchorEngine<JsonOpBatch>, JsonPathAnchorEngine>();

        // Anchor engine registry (maps engine-type-string → adapter for CompactWithAnchorsService).
        services.TryAddSingleton<IAnchorEngineRegistry, AnchorEngineRegistry>();

        // Compact + anchor-rebase service — hooks into CompactDocument to keep anchors safe.
        services.TryAddSingleton<CompactWithAnchorsService>();

        // Default authorizer — allows everything.
        // The router will log a warning at startup if this default is still active.
        services.TryAddScoped<IDocumentAuthorizer, AllowAllAuthorizer>();

        // Default management authorizer — denies everything (fail-closed).
        // The host must replace this via UseDatabaseCommandAuthorization<T>().
        services.TryAddScoped<IDatabaseCommandAuthorizer, DenyAllDatabaseCommandAuthorizer>();

        // Default document seeder — creates empty documents.
        services.TryAddScoped(typeof(IDocumentSeeder<>), typeof(EmptyDocumentSeeder<>));

        // History manager
        services.TryAddSingleton(typeof(OpStream.Server.History.HistoryManager<,>));

        // Multi-tenancy
        services.TryAddSingleton<ITenantProvider, DefaultTenantProvider>();
        services.TryAddSingleton<IDocumentIdGlobalizer, TenantAwareDocumentIdGlobalizer>();

        // Default engine: plain text
        builder.AddEngine<TextDocument, TextOp, TextOtEngine>("text");
        builder.AddAnchorEngine<TextOp>("text");
        builder.AddAnchorEngine<RichTextOp>("richtext");
        builder.AddAnchorEngine<JsonOpBatch>("json");

        // Default health checks — Memory/Noop variants. Replaced when the user
        // wires a real provider via UseRedisStorage / UseRedisBackplane etc.
        services.AddHealthChecks()
            .AddCheck<MemoryStorageHealthCheck>(
                name: "opstream-storage",
                failureStatus: HealthStatus.Degraded,
                tags: new[] { "opstream", "storage" })
            .AddCheck<LocalBackplaneHealthCheck>(
                name: "opstream-backplane",
                failureStatus: HealthStatus.Healthy,
                tags: new[] { "opstream", "backplane" });

        return builder;
    }

    /// <inheritdoc cref="AddOpStream(IServiceCollection,Action{OpStreamOptions}?)"/>
    [Obsolete("Use AddOpStream() instead. This alias will be removed in v1.0.", error: false)]
    public static IOpStreamBuilder AddOpStreamServer(
        this IServiceCollection services,
        Action<OpStreamOptions>? configure = null)
        => services.AddOpStream(configure);

    // =========================================================================
    // Add* — collection-style (call N times to register N items)
    // =========================================================================

    /// <summary>
    /// Registers an engine and its document session factory for a given document type.
    /// Call once per document type; multiple document types are fully supported.
    /// </summary>
    /// <typeparam name="TDoc">The strongly-typed document state.</typeparam>
    /// <typeparam name="TOp">The strongly-typed operation.</typeparam>
    /// <typeparam name="TEngine">Concrete <see cref="IOpEngine{TDoc,TOp}"/> implementation.</typeparam>
    /// <param name="documentType">
    /// The type discriminator string sent by the client (e.g. <c>"text"</c>, <c>"rich-text"</c>).
    /// </param>
    public static IOpStreamBuilder AddEngine<TDoc, TOp, TEngine>(
        this IOpStreamBuilder builder,
        string documentType)
        where TEngine : class, IOpEngine<TDoc, TOp>
    {
        builder.Services.AddSingleton<IOpEngine<TDoc, TOp>, TEngine>();
        builder.Services.AddSingleton<IDocumentSessionFactory>(sp =>
        {
            var engine = sp.GetRequiredService<IOpEngine<TDoc, TOp>>();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            return new TypedDocumentSessionFactory<TDoc, TOp>(documentType, sp, engine, scopeFactory, loggerFactory);
        });
        return builder;
    }

    /// <inheritdoc cref="AddEngine{TDoc,TOp,TEngine}(IOpStreamBuilder,string)"/>
    [Obsolete("Use AddEngine<TDoc, TOp, TEngine>(documentType) instead. This alias will be removed in v1.0.", error: false)]
    public static IOpStreamBuilder ConfigureDocumentType<TDoc, TOp, TEngine>(
        this IOpStreamBuilder builder,
        string documentType)
        where TEngine : class, IOpEngine<TDoc, TOp>
        => builder.AddEngine<TDoc, TOp, TEngine>(documentType);

    /// <summary>
    /// Adds a per-operation validator. Multiple validators are all evaluated in registration order.
    /// </summary>
    public static IOpStreamBuilder AddValidator<TOp, TValidator>(this IOpStreamBuilder builder)
        where TValidator : class, IOpValidator<TOp>
    {
        builder.Services.AddScoped<IOpValidator<TOp>, TValidator>();
        return builder;
    }

    /// <summary>
    /// Registers a mapping from <paramref name="documentType"/> to the <see cref="IAnchorEngine{TOp}"/>
    /// already in DI, so <see cref="CompactWithAnchorsService"/> can rebase anchors during compaction.
    /// Call this alongside <c>AddEngine&lt;TDoc, TOp, TEngine&gt;(documentType)</c> for every engine
    /// that has a corresponding <c>IAnchorEngine&lt;TOp&gt;</c> registered.
    /// </summary>
    public static IOpStreamBuilder AddAnchorEngine<TOp>(
        this IOpStreamBuilder builder,
        string documentType)
    {
        builder.Services.AddSingleton<AnchorEngineRegistration>(sp =>
        {
            var engine = sp.GetRequiredService<IAnchorEngine<TOp>>();
            IAnchorEngineAdapter adapter = new AnchorEngineAdapter<TOp>(engine);
            return new AnchorEngineRegistration(documentType, adapter);
        });
        return builder;
    }

    /// <summary>
    /// Registers a custom document seeder used to populate new documents on first access.
    /// </summary>
    public static IOpStreamBuilder UseSeeder<TDoc, TSeeder>(this IOpStreamBuilder builder)
        where TSeeder : class, IDocumentSeeder<TDoc>
    {
        builder.Services.AddScoped<IDocumentSeeder<TDoc>, TSeeder>();
        return builder;
    }

    /// <summary>
    /// Registers a handler invoked when a document loses its last peer (it "drains"). The
    /// handler receives the final, full document state — for example to persist it into the
    /// host's own database — and may return <c>DocumentDrainDecision.Delete</c> to have
    /// OpStream permanently remove the document and all of its data (state, op log, snapshots
    /// and history).
    /// <para>
    /// Multiple handlers may be registered; they all run when a document drains, and if any of
    /// them asks for deletion the document is deleted. Handlers are resolved per drain in a
    /// fresh scope, so they may depend on scoped services such as a <c>DbContext</c>.
    /// </para>
    /// </summary>
    public static IOpStreamBuilder AddDocumentDrainHandler<THandler>(this IOpStreamBuilder builder)
        where THandler : class, IDocumentDrainHandler
    {
        builder.Services.AddScoped<IDocumentDrainHandler, THandler>();
        return builder;
    }

    /// <summary>
    /// Registers a 3-way merge driver for the given engine type so that
    /// <see cref="VersioningRouter.MergeAsync"/> can merge branches of that type.
    /// Call alongside <see cref="AddEngine{TDoc,TOp,TEngine}"/> for every engine that should support merge.
    /// </summary>
    public static IOpStreamBuilder UseVersioningMerge<TDoc, TOp>(
        this IOpStreamBuilder builder,
        string engineType)
    {
        builder.Services.AddSingleton<IDocumentMergeDriver>(sp =>
            new DocumentMergeDriver<TDoc, TOp>(
                engineType,
                sp.GetRequiredService<IOpEngine<TDoc, TOp>>(),
                sp.GetRequiredService<IServiceScopeFactory>()));
        return builder;
    }

    // =========================================================================
    // Use* — singleton-style (last call wins)
    // =========================================================================

    /// <summary>
    /// Replaces the current storage with the in-memory store.
    /// Useful to be explicit in test or dev configuration blocks.
    /// <para><b>Not for production</b> — data is lost when the process restarts.</para>
    /// </summary>
    public static IOpStreamBuilder UseMemoryStorage(this IOpStreamBuilder builder)
    {
        builder.Services.TryAddSingleton<MemoryDocumentStore>();
        builder.Services.Replace(ServiceDescriptor.Singleton<IDocumentStore>(
            sp => sp.GetRequiredService<MemoryDocumentStore>()));
        builder.Services.Replace(ServiceDescriptor.Singleton<IHistoryStore>(
            sp => sp.GetRequiredService<MemoryDocumentStore>()));
        return builder;
    }

    /// <summary>
    /// Keeps the default single-node, in-process backplane (<see cref="LocalBackplane"/>).
    /// Publishes are delivered to every subscriber on the same node, so peers
    /// connected to a single instance receive each other's updates.
    /// Useful to be explicit when only one node will ever run.
    /// </summary>
    public static IOpStreamBuilder UseLocalBackplane(this IOpStreamBuilder builder)
    {
        builder.Services.Replace(ServiceDescriptor.Singleton<IBackplane, LocalBackplane>());
        builder.Services.Replace(ServiceDescriptor.Singleton<IDocumentOwnershipManager, LocalDocumentOwnershipManager>());
        return builder;
    }

    /// <inheritdoc cref="UseLocalBackplane(IOpStreamBuilder)"/>
    [Obsolete("Use UseLocalBackplane() instead. The previous NoopBackplane name was misleading — it did nothing and broke single-node fan-out. This alias will be removed in v1.0.", error: false)]
    public static IOpStreamBuilder UseNoopBackplane(this IOpStreamBuilder builder)
        => builder.UseLocalBackplane();

    /// <summary>
    /// Registers a custom <see cref="IDocumentAuthorizer"/> that integrates with the host
    /// application's identity and permission model.
    /// Replaces the default <see cref="AllowAllAuthorizer"/>.
    /// </summary>
    public static IOpStreamBuilder UseAuthorization<TAuthorizer>(this IOpStreamBuilder builder)
        where TAuthorizer : class, IDocumentAuthorizer
    {
        builder.Services.Replace(ServiceDescriptor.Scoped<IDocumentAuthorizer, TAuthorizer>());
        return builder;
    }

    /// <summary>
    /// Registers a custom <see cref="IDatabaseCommandAuthorizer"/> that decides which management
    /// commands (list / delete / compact / purge) the current caller may execute.
    /// Replaces the default <c>DenyAllDatabaseCommandAuthorizer</c>.
    /// </summary>
    public static IOpStreamBuilder UseDatabaseCommandAuthorization<TAuthorizer>(this IOpStreamBuilder builder)
        where TAuthorizer : class, IDatabaseCommandAuthorizer
    {
        builder.Services.Replace(ServiceDescriptor.Scoped<IDatabaseCommandAuthorizer, TAuthorizer>());
        return builder;
    }

    /// <summary>
    /// Overrides the snapshot policy (default: 100 ops or 5 minutes, whichever comes first).
    /// </summary>
    public static IOpStreamBuilder UseSnapshotPolicy(this IOpStreamBuilder builder, ISnapshotPolicy policy)
    {
        builder.Services.Replace(ServiceDescriptor.Singleton(policy));
        return builder;
    }
}
