using System;
using System.Threading;
using System.Threading.Tasks;

namespace TFM.Nucleus.Abstractions.Interfaces
{
    /// <summary>
    /// Reads what a load left for later on the objects it loaded: the entity a <c>[LinkedObject]</c>
    /// property refers to, the first time the property is read, and the members of a linked
    /// collection, the first time one is read. A load stores only the referenced entity's ID and
    /// reads no members; the stack that loaded the object (a manager or a data context) is bound to
    /// it as its loader, so these reads come from the same database, never from the ambient runtime
    /// of another stack.
    /// </summary>
    public interface ILinkedObjectLoader
    {
        /// <summary>
        /// The stored <paramref name="type"/> entity with the ID <paramref name="id"/>, on the
        /// synchronous path; null when no such row exists (or it is soft deleted).
        /// </summary>
        ICoreObject? LoadLinkedObject(Type type, Guid id);

        /// <summary>
        /// Loads a linked collection's stored members on the synchronous path and attaches them;
        /// does nothing when it is loaded.
        /// </summary>
        void LoadLinkedCollection(INucleusLinkedCollection collection) =>
            throw new NotSupportedException($"{GetType().Name} cannot load linked collections.");

        /// <inheritdoc cref="LoadLinkedCollection"/>
        Task LoadLinkedCollectionAsync(INucleusLinkedCollection collection, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException($"{GetType().Name} cannot load linked collections.");
    }
}
