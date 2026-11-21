using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TFM.Nucleus.Data;

namespace TFM.Nucleus.Abstractions.Interfaces
{
    public interface INucleusChildCollection
    {
        IQuery? GetInsertUpdateQuery();
        public IObjectSchema ObjectSchema { get; }

        public Type ChildType { get; }
        public IEnumerable<ICoreObject> ChildObjects { get; }

        /// <summary>
        /// Adds children read from storage without marking anything changed: each child's parent
        /// is bound as its stored value and no collection event is raised. A child already in
        /// the collection (by ID) is skipped. Nucleus calls this while materialising.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void AttachLoaded(IEnumerable<ICoreObject> children) =>
            throw new NotSupportedException($"{GetType().Name} cannot attach loaded children.");

        /// <summary>
        /// False when the parent was loaded with this collection excluded: its rows exist in
        /// storage but not here, so reading it throws until it is loaded.
        /// </summary>
        public bool IsLoaded => true;

        /// <summary>Marks the collection as not loaded. Nucleus calls this for an excluded collection.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void MarkUnloaded() =>
            throw new NotSupportedException($"{GetType().Name} cannot be left unloaded.");

        /// <summary>
        /// Marks the collection as not loaded. A lazy one (a [RetrieveOnDemand] collection the
        /// load left out) loads itself the first time it is read; an excluded one throws until it
        /// is loaded. Nucleus calls this while materialising.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void MarkUnloaded(bool lazy)
        {
            if (lazy)
                throw new NotSupportedException($"{GetType().Name} cannot load itself on demand.");
            MarkUnloaded();
        }

        /// <summary>
        /// Marks the collection as lazy and not loaded, to be loaded by <paramref name="load"/>
        /// the first time it is read: the loader of the stack that loaded the parent, so the
        /// collection reads from the same database whatever the ambient runtime is.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void MarkLazy(Action<INucleusChildCollection> load) => MarkUnloaded(lazy: true);

        /// <summary>
        /// The stored children removed from the collection since the parent was last saved. A
        /// save of the parent deletes their rows, in the same transaction as its other writes.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public IEnumerable<ICoreObject> RemovedObjects => [];

        /// <summary>Forgets the removals once the save that deleted them has committed.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void AcknowledgeRemovals()
        {
        }

        /// <summary>
        /// Puts back removals that <see cref="AcknowledgeRemovals"/> forgot, because the
        /// transaction that deleted them rolled back. A child added to the collection again since
        /// is left where it is.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public void RestoreRemovals(IEnumerable<ICoreObject> removed)
        {
        }

        /// <summary>The object that owns the collection.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public ICoreObject ParentObject =>
            throw new NotSupportedException($"{GetType().Name} does not expose its parent.");

        /// <summary>
        /// The object the collection belongs to. The same name a linked collection uses, so code
        /// that handles both reads one member.
        /// </summary>
        public ICoreObject Owner => ParentObject;

        /// <summary>The type of the collection's items: <see cref="ChildType"/> under the name a linked collection shares.</summary>
        public Type ItemType => ChildType;

        /// <summary>The items held: <see cref="ChildObjects"/> under the name a linked collection shares.</summary>
        public IEnumerable<ICoreObject> Items => ChildObjects;

        /// <summary>The stored items removed since the last save: <see cref="RemovedObjects"/> under the name a linked collection shares.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public IEnumerable<ICoreObject> RemovedItems => RemovedObjects;

        /// <summary>
        /// Loads a collection that was left out when its owner was loaded (excluded, or lazy). A
        /// loaded collection is left as it is.
        /// </summary>
        public Task LoadAsync(System.Threading.CancellationToken cancellationToken = default) =>
            throw new NotSupportedException($"{GetType().Name} cannot load itself.");
    }

    public interface INucleusChildCollection<TChild, TParent> : INucleusChildCollection,  IEnumerable<TChild>, INotifyCollectionChanged, INotifyPropertyChanged, IDisposable
    {
    }
}
