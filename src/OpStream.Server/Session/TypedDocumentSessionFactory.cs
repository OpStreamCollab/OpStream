using OpStream.Constants;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using OpStream.Server.Engine;
using OpStream.Server.Models;
using OpStream.Server.Session.Snapshots;
using OpStream.Server.Storage;
using OpStream.Shared.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpStream.Server.Session
{
    internal class TypedDocumentSessionFactory<TDoc, TOp> : IDocumentSessionFactory
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IOpEngine<TDoc, TOp> _engine;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILoggerFactory _loggerFactory;

        public string DocumentType { get; }

        public TypedDocumentSessionFactory(
            string documentType,
            IServiceProvider serviceProvider,
            IOpEngine<TDoc, TOp> engine,
            IServiceScopeFactory scopeFactory,
            ILoggerFactory loggerFactory)
        {
            DocumentType = documentType;
            _serviceProvider = serviceProvider;
            _engine = engine;
            _scopeFactory = scopeFactory;
            _loggerFactory = loggerFactory;
        }

        public async Task<IDocumentSession> CreateSessionAsync(
     string documentId,
     long initialRevision,
     ReadOnlyMemory<byte>? snapshotData,
     CancellationToken ct)
        {
            TDoc currentState;
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            var seeder = scope.ServiceProvider.GetRequiredService<IDocumentSeeder<TDoc>>();

            if (snapshotData.HasValue && !snapshotData.Value.IsEmpty)
            {
                currentState = JsonSerializer.Deserialize<TDoc>(snapshotData.Value.Span, OpStreamJsonOptions.Default)!;
            }
            else
            {
                // Llamamos al Seeder del Host
                var seededState = await seeder.GetInitialStateAsync(documentId, ct);
                if (seededState == null)
                {
                    throw new InvalidOperationException($"El documento {documentId} no existe y no pudo ser inicializado.");
                }
                currentState = seededState;


                var stateBytes = JsonSerializer.SerializeToUtf8Bytes(currentState, OpStreamJsonOptions.Default);
                await store.WriteSnapshotAsync(documentId, new DocumentSnapshot(0, DateTimeOffset.UtcNow, stateBytes), ct);
            }
            var backplane = _serviceProvider.GetRequiredService<IBackplane>();
            var snapshotter = _serviceProvider.GetRequiredService<IOpSnapshotter>();
            var historySnapshotter = _serviceProvider.GetRequiredService<IOpHistorySnapshotter>();
            var validators = _serviceProvider.GetServices<IOpValidator<TOp>>();
            var postApplyHooks = _serviceProvider.GetServices<IPostApplyHook<TOp>>();
            var logger = _loggerFactory.CreateLogger<DocumentSession<TDoc, TOp>>();

            return new DocumentSession<TDoc, TOp>(
                documentId, currentState, _engine, initialRevision, store, backplane,
                snapshotter, historySnapshotter, validators, logger, postApplyHooks);
        }
    }
}
