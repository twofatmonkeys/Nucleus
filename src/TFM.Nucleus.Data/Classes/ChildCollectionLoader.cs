using System;
using System.Threading;
using System.Threading.Tasks;
using TFM.Nucleus.Abstractions.Interfaces;

namespace TFM.Nucleus.Data
{
    /// <summary>
    /// Loads a child collection that its parent's load left out (excluded, or lazy): the rows of
    /// the child type whose parent is the collection's owner, with their own non-lazy children,
    /// attached as loaded and merged by ID with any children added since. The manager and the
    /// data context both load through here, each with its own provider, so a lazy collection
    /// reads from the stack that loaded its parent rather than from the ambient runtime.
    /// </summary>
    public static class ChildCollectionLoader
    {
        /// <summary>
        /// Loads the collection on the synchronous path; does nothing when it is loaded. The
        /// children's <c>[LinkedObject]</c> properties are read through the same provider.
        /// </summary>
        public static void Load(IDataProvider provider, INucleusChildCollection collection) =>
            Load(provider, collection, null);

        /// <inheritdoc cref="Load(IDataProvider, INucleusChildCollection)"/>
        /// <param name="provider">The provider that stores the child type.</param>
        /// <param name="collection">The collection to load.</param>
        /// <param name="linkLoader">
        /// What the children's <c>[LinkedObject]</c> properties and linked collections are read
        /// through: the stack that loaded the parent. The same provider when null.
        /// </param>
        public static void Load(IDataProvider provider, INucleusChildCollection collection, ILinkedObjectLoader? linkLoader)
        {
            ArgumentNullException.ThrowIfNull(provider);
            ArgumentNullException.ThrowIfNull(collection);
            if (collection.IsLoaded)
                return;
            linkLoader ??= new ProviderLinkedObjectLoader(provider);
            var children = KeyedRowLoader.Rows(provider, collection.ChildType, ParentPropertyOf(collection), collection.ParentObject.ID, linkLoader);
            collection.AttachLoaded(children);
            KeyedRowLoader.CompleteTaken(collection.ChildObjects, children, linkLoader);
        }

        /// <inheritdoc cref="Load(IDataProvider, INucleusChildCollection)"/>
        public static Task LoadAsync(IDataProvider provider, INucleusChildCollection collection, CancellationToken token = default) =>
            LoadAsync(provider, collection, null, token);

        /// <inheritdoc cref="Load(IDataProvider, INucleusChildCollection, ILinkedObjectLoader?)"/>
        public static async Task LoadAsync(IDataProvider provider, INucleusChildCollection collection, ILinkedObjectLoader? linkLoader,
            CancellationToken token = default)
        {
            ArgumentNullException.ThrowIfNull(provider);
            ArgumentNullException.ThrowIfNull(collection);
            if (collection.IsLoaded)
                return;
            linkLoader ??= new ProviderLinkedObjectLoader(provider);
            var children = await KeyedRowLoader.RowsAsync(provider, collection.ChildType, ParentPropertyOf(collection), collection.ParentObject.ID,
                linkLoader, token).ConfigureAwait(false);
            collection.AttachLoaded(children);
            KeyedRowLoader.CompleteTaken(collection.ChildObjects, children, linkLoader);
        }

        /// <summary>The child type's parent property, the key its rows are read by.</summary>
        private static string ParentPropertyOf(INucleusChildCollection collection) =>
            collection.ObjectSchema.ParentObjectProperty?.Name
            ?? throw new InvalidOperationException($"{collection.ChildType.Name} is used as a child collection but has no [ParentObject] property.");
    }
}
