using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace TFM.Nucleus.Abstractions.Interfaces
{
    /// <summary>
    /// The members of a one-to-many relationship between two root types, held on the one: the
    /// stored rows of the many type whose key property (named by <c>[LinkedBy]</c>) holds the
    /// one's ID. Unlike a child collection, a member is a root of its own: it loads, saves and is
    /// deleted on its own as well as through the one.
    /// </summary>
    public interface INucleusLinkedCollection
    {
        /// <summary>The one the members belong to.</summary>
        ICoreObject Owner { get; }

        /// <summary>The many type.</summary>
        Type MemberType { get; }

        /// <summary>The name of the one's collection property.</summary>
        string PropertyName { get; }

        /// <summary>
        /// False while the stored members have not been read: the collection loads them, in one
        /// query, the first time a member is read, or when <see cref="LoadAsync"/> runs.
        /// </summary>
        bool IsLoaded { get; }

        /// <summary>Loads the stored members on the asynchronous path; does nothing when they are loaded.</summary>
        Task LoadAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// The members held here, for saving and committing. Before the collection is loaded these
        /// are only the members added since, which is exactly what a save must write, so this
        /// never loads it.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        IEnumerable<ICoreObject> Members { get; }

        /// <summary>The stored members removed since the one was last saved, whose rows its save deletes.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        IEnumerable<ICoreObject> RemovedMembers { get; }

        /// <summary>Forgets the removals once the save that deleted them has committed.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        void AcknowledgeRemovals();

        /// <summary>
        /// Adds members read from storage without marking anything changed, and marks the
        /// collection loaded. A member already held (by ID) is skipped. Nucleus calls this when it
        /// loads the collection.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        void AttachLoaded(IEnumerable<ICoreObject> members);
    }

    /// <inheritdoc cref="INucleusLinkedCollection"/>
    public interface INucleusLinkedCollection<TMany, TOne> : INucleusLinkedCollection, IEnumerable<TMany>, INotifyCollectionChanged
    {
    }
}
