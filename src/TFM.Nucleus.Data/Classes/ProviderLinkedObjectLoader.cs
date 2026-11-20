using System;
using System.Threading;
using System.Threading.Tasks;
using TFM.Nucleus.Abstractions.Interfaces;

namespace TFM.Nucleus.Data
{
    /// <summary>
    /// Reads what a load left for later through one provider, for the objects a data context
    /// loaded (and the children their lazy collections load): a <c>[LinkedObject]</c> on its first
    /// read, and a linked collection's members on theirs. Both come from that provider, so a type
    /// it does not store fails with the provider's own message. The reads run on a context of
    /// their own, since the context that loaded the object may be gone by the time they happen.
    /// </summary>
    internal sealed class ProviderLinkedObjectLoader(IDataProvider provider) : ILinkedObjectLoader
    {
        private readonly IDataProvider _provider = provider ?? throw new ArgumentNullException(nameof(provider));

        public ICoreObject? LoadLinkedObject(Type type, Guid id)
        {
            ArgumentNullException.ThrowIfNull(type);
            if (id == Guid.Empty)
                return null;
            var rows = KeyedRowLoader.Rows(_provider, type, nameof(ICoreObject.ID), id, this);
            if (rows.Count == 0)
                return null;
            ChildGraphBinder.Complete(rows[0], [], this);
            return rows[0];
        }

        public void LoadLinkedCollection(INucleusLinkedCollection collection) =>
            LinkedCollectionLoader.Load(_provider, collection, this);

        public Task LoadLinkedCollectionAsync(INucleusLinkedCollection collection, CancellationToken cancellationToken = default) =>
            LinkedCollectionLoader.LoadAsync(_provider, collection, this, cancellationToken);
    }
}
