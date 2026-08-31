using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus
{
    [AttributeUsage(AttributeTargets.Class,
            Inherited = false)]
    public class Cacheable : Attribute
    {
        public int RefreshInterval = 120;
        public int CacheLifetime = 900;
        public Cacheable()
        {

        }
        public Cacheable(int refreshInterval, int cacheLifetime)
        {
            RefreshInterval = refreshInterval;
            CacheLifetime = cacheLifetime;
        }
    }

    [AttributeUsage(AttributeTargets.Class,
        Inherited = false)]
    public class SuppressDB : Attribute
    { }

    /// <summary>
    /// Establishes the initialisation order of entity types at program load, descending.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public class InitializePriority : Attribute
    {
        public int Priority = 0;
        public InitializePriority() : this(0) { }
        public InitializePriority(int priority) { Priority = priority; }
    }

    /// <summary>Reserved for per-property lazy loading, which is not yet implemented.</summary>
    [AttributeUsage(AttributeTargets.Property, Inherited = false)]
    public class RetrieveOnDemand : Attribute
    { }

    /// <summary>Includes the linked entity's ID in the audit entry for this property.</summary>
    [AttributeUsage(AttributeTargets.Property, Inherited = false)]
    public class LinkWhenLogging : Attribute
    { }

    /// <summary>Records that the property changed without recording the values.</summary>
    [AttributeUsage(AttributeTargets.Property, Inherited = false)]
    public class AbbreviatedLogging : Attribute
    { }

    /// <summary>
    /// Omits the type or property from audit logging. Deliberately inherited: a base type that
    /// opts out of logging is making a decision about its data, and a derived type should have to
    /// opt back in explicitly rather than start logging it by default.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, Inherited = true)]
    public class SuppressLogging : Attribute
    { }

    [AttributeUsage(AttributeTargets.Property, Inherited = false)]
    public class LinkedObject : Attribute
    {
        public readonly string? LinkedIDProperty;
        public readonly bool ForceSave = false;

        public LinkedObject() { }
        public LinkedObject(bool forceSave)
        {
            ForceSave = forceSave;
        }
        public LinkedObject(string linkedIDProperty)
        {
            LinkedIDProperty = linkedIDProperty;
        }
        public LinkedObject(string linkedIDProperty, bool forceSave)
        {
            LinkedIDProperty = linkedIDProperty;
            ForceSave = forceSave;
        }
    }

    [AttributeUsage(AttributeTargets.All,
            Inherited = false)]
    public class LinkedChildCollection : Attribute
    { }

    [AttributeUsage(AttributeTargets.All, Inherited = false)]
    public class ParentObject : Attribute
    {
        public readonly string? ParentIDProperty;
        public ParentObject()
        { }
        public ParentObject(string parentIDProperty)
        {
            ParentIDProperty = parentIDProperty;
        }
    }

}
