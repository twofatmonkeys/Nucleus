using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using TFM.Nucleus.Abstractions.Interfaces;

namespace TFM.Nucleus.Data
{
    /// <summary>
    /// Loads a <c>[LinkedObject]</c> through one provider, for the objects a data context loaded
    /// (and the children their lazy collections load): those are read from that provider, so a
    /// reference to a type it does not store fails with the provider's own message. The read runs
    /// on a context of its own, since the context that loaded the object may be gone by the time
    /// the property is read. Retries and timeouts are the provider's: a connection open retries a
    /// transient failure, and the command runs under the provider's command timeout.
    /// </summary>
    internal sealed class ProviderLinkedObjectLoader(IDataProvider provider) : ILinkedObjectLoader
    {
        private static readonly ConcurrentDictionary<Type, MethodInfo> _byType = [];

        private readonly IDataProvider _provider = provider ?? throw new ArgumentNullException(nameof(provider));

        public ICoreObject? LoadLinkedObject(Type type, Guid id)
        {
            ArgumentNullException.ThrowIfNull(type);
            if (id == Guid.Empty)
                return null;
            var load = _byType.GetOrAdd(type, static t =>
                typeof(ProviderLinkedObjectLoader).GetMethod(nameof(Load), BindingFlags.NonPublic | BindingFlags.Instance)!.MakeGenericMethod(t));
            return (ICoreObject?)load.Invoke(this, BindingFlags.DoNotWrapExceptions, binder: null, [id], culture: null);
        }

        private ICoreObject? Load<T>(Guid id) where T : ICoreObject<T>, new()
        {
            var query = _provider.ParseSelectExpression<T>(Expression.Constant(true));
            var idColumn = query.DataTableSchema?.FindByPropertyName(nameof(ICoreObject.ID))?.ColumnName ?? nameof(ICoreObject.ID);
            query.AddWhere(new Parameter(idColumn, id));
            using var context = _provider.CreateContext();
            var loaded = ChildGraphBinder.BuildCollection<T>(query, context.GetDataSet(query),
                collection => ChildCollectionLoader.Load(_provider, collection, this), this, []);
            return loaded.FirstOrDefault();
        }
    }
}
