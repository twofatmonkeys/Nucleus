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
    /// Attribute that establishes the loading/initializing sequence of TFM classes at program load in Descending order.
    /// </summary>
    public class InitializePriority : Attribute
    {
        public int Priority = 0;
        public InitializePriority() : this(0) { }
        public InitializePriority(int priority) { Priority = priority; }
    }
    public class RetrieveOnDemand : Attribute
    { }
    public class LinkWhenLogging : Attribute
    { }
    public class AbbreviatedLogging : Attribute
    { }
    public class SuppressLogging : Attribute
    { }
    public class SuppressCopying : Attribute
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
