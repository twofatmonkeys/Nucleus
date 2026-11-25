using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace TFM.Nucleus.Data.Providers
{
    /// <summary>
    /// What the manager is told about the work of a context it handed to a caller, so that work
    /// gets what a manager save gets: the changes audited and the <c>[Cacheable]</c> caches of the
    /// written types evicted, both only once the work has committed. A context a provider hands
    /// out directly has no observer and does neither.
    /// </summary>
    internal interface IContextObserver
    {
        /// <summary>The objects a save has written, before they are marked saved, while what they changed can still be read.</summary>
        void Saving(IEnumerable<ICoreObject?> objects);

        /// <summary>A delete of rows of <paramref name="type"/> has run.</summary>
        void Deleted(Type type);

        /// <summary>The logger an object's changes are audited through, or null for none. What it is given is held until <see cref="Committed"/>.</summary>
        ILogger? AuditLoggerFor(Type type);

        /// <summary>The work reported so far is committed: its transaction committed, or it ran without one.</summary>
        void Committed();

        /// <summary>The work reported so far was rolled back, or its commit failed.</summary>
        void Abandoned();
    }

    /// <summary>A context that reports its work to an <see cref="IContextObserver"/>. A child reports to its root's.</summary>
    internal interface IObservedContext
    {
        IContextObserver? Observer { set; }
    }
}
