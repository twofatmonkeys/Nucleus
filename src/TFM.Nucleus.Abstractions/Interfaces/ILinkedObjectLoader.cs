using System;

namespace TFM.Nucleus.Abstractions.Interfaces
{
    /// <summary>
    /// Loads the entity a loaded object's <c>[LinkedObject]</c> property refers to, the first time
    /// the property is read. A load stores only the referenced entity's ID; the stack that loaded
    /// the object (a manager or a data context) is bound to it as its loader, so the reference is
    /// read from the same database, never from the ambient runtime of another stack.
    /// </summary>
    public interface ILinkedObjectLoader
    {
        /// <summary>
        /// The stored <paramref name="type"/> entity with the ID <paramref name="id"/>, on the
        /// synchronous path; null when no such row exists (or it is soft deleted).
        /// </summary>
        ICoreObject? LoadLinkedObject(Type type, Guid id);
    }
}
