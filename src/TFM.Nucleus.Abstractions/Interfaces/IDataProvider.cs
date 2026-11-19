using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus.Data
{
    public interface IDataProvider
    {
        public Guid ConnectionIdentifier { get; }
        public DataProviderStatus Status { get; }

        /// <summary>Dialect-neutral provider facts used by the neutral layers for routing and batching.</summary>
        public DataProviderCapabilities Capabilities { get; }

        /// <summary>
        /// The entity base type this provider is registered for; entities resolve to the provider
        /// of their nearest registered root. Null means the provider is the unrooted default.
        /// </summary>
        public Type? RootType { get; set; }

        public DataProviderStatus Initialize(IEnumerable<IObjectSchema>? objectSchemas = null);
        public Task<DataProviderStatus> InitializeAsync(IObjectSchemaProvider? objectSchema = null);

        public ISelectQuery ParseSelectExpression<T>(Expression expression);
        public IUpdateQuery? CreateUpdateQuery<T>(IEnumerable<T>? objs) where T : ICoreObject<T>, new();
        public IInsertQuery? CreateInsertQuery<T>(IEnumerable<T>? objs) where T : ICoreObject<T>, new();
        public IQuery? CreateInsertAndUpdateQuery<T>(IEnumerable<T>? objs) where T : ICoreObject<T>, new();

        /// <summary>
        /// Every statement a save of <paramref name="objs"/> runs, in the order they must run in
        /// one transaction: the deletes of children removed from their collections, then the
        /// writes of the forceSave links, then the objects' own inserts and updates with their
        /// child collections. Empty when there is nothing to write. A provider that keeps no
        /// graph writes the objects alone.
        /// </summary>
        public IReadOnlyList<IQuery> CreateSaveQueries<T>(IEnumerable<T>? objs) where T : ICoreObject<T>, new() =>
            CreateInsertAndUpdateQuery(objs) is { } query ? [query] : [];
        public IDeleteQuery? CreateDeleteQuery<T>(IEnumerable<T>? objs) where T : ICoreObject<T>, new();

        /// <summary>Subscribes to external-change notifications for an entity type. Throws NotSupportedException when Capabilities.SupportsChangeNotifications is false.</summary>
        public Task<IChangeSubscription> SubscribeToChangesAsync<T>(CancellationToken token = default) where T : ICoreObject<T>, new();

        public IDataContext CreateContext(ContextOptions? options = null);
        public Task<IDataContext> CreateContextAsync(ContextOptions? options = null, CancellationToken token = default);

        public Task<DataProviderStatus> CheckDataContextAsync();


        
        public DataProviderStatus CheckSchemaIsCurrent(IEnumerable<IObjectSchema>? objectSchemas);
        public Task<DataProviderStatus> CheckSchemaIsCurrentAsync(IEnumerable<IObjectSchema>? objectSchemas);
        
        public void UpdateSchema(IEnumerable<IObjectSchema> objectSchemas);
        public Task UpdateSchemaAsync(IObjectSchema objectSchema);
        public Task UpdateSchemaAsync(IEnumerable<IObjectSchema> objectSchemas);

    }

    public enum DataProviderStatus
    {
        NotConfigured,
        Uninitialized,
        Initializing,
        NotAvailable,
        Ok,
        Error,
        IncompatibleSchema,
        Unknown,
    }

    public enum DataProviderConnectionStatus
    {
        NotConfigured,
        Connected,
        AuthenticationError,
        NetworkError,
        Error
    }
}
