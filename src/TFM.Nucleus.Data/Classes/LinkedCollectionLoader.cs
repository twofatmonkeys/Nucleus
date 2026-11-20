using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TFM.Nucleus.Abstractions.Interfaces;

namespace TFM.Nucleus.Data
{
    /// <summary>
    /// Loads a linked collection's stored members: the rows of the many type whose key holds the
    /// one's ID, with their own non-lazy children, attached as loaded and merged by ID with any
    /// members added since. The manager loads through here with the provider that stores the many
    /// type, and a data context with its own provider.
    /// </summary>
    internal static class LinkedCollectionLoader
    {
        /// <summary>Loads the members on the synchronous path; does nothing when they are loaded.</summary>
        public static void Load(IDataProvider provider, INucleusLinkedCollection collection, ILinkedObjectLoader linkLoader)
        {
            ArgumentNullException.ThrowIfNull(provider);
            ArgumentNullException.ThrowIfNull(collection);
            if (collection.IsLoaded)
                return;
            var members = KeyedRowLoader.Rows(provider, collection.MemberType, KeyOf(collection), collection.Owner.ID, linkLoader);
            collection.AttachLoaded(members);
            KeyedRowLoader.CompleteTaken(collection.Members, members, linkLoader);
        }

        /// <inheritdoc cref="Load"/>
        public static async Task LoadAsync(IDataProvider provider, INucleusLinkedCollection collection, ILinkedObjectLoader linkLoader, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(provider);
            ArgumentNullException.ThrowIfNull(collection);
            if (collection.IsLoaded)
                return;
            var members = await KeyedRowLoader.RowsAsync(provider, collection.MemberType, KeyOf(collection), collection.Owner.ID, linkLoader, token)
                .ConfigureAwait(false);
            collection.AttachLoaded(members);
            KeyedRowLoader.CompleteTaken(collection.Members, members, linkLoader);
        }

        /// <summary>The many type's key property the collection is kept by.</summary>
        private static string KeyOf(INucleusLinkedCollection collection)
        {
            var schema = collection.Owner.ObjectSchema;
            var property = schema.LinkedCollectionProperties.FirstOrDefault(p => p.Name == collection.PropertyName)
                ?? throw new InvalidOperationException($"{schema.Name}.{collection.PropertyName} is not a linked collection.");
            return schema.GetLinkedCollectionKey(property).Name;
        }
    }
}
