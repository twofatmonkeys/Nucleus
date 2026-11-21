using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus
{
    [AttributeUsage(AttributeTargets.Class,
            Inherited = false)]
    public class CacheableAttribute : Attribute
    {
        public int RefreshInterval = 120;
        public int CacheLifetime = 900;
        public CacheableAttribute()
        {

        }
        public CacheableAttribute(int refreshInterval, int cacheLifetime)
        {
            RefreshInterval = refreshInterval;
            CacheLifetime = cacheLifetime;
        }
    }

    [AttributeUsage(AttributeTargets.Class,
        Inherited = false)]
    public class NotStoredAttribute : Attribute
    { }

    /// <summary>
    /// Establishes the initialisation order of entity types at program load, descending.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public class InitializePriorityAttribute : Attribute
    {
        public int Priority = 0;
        public InitializePriorityAttribute() : this(0) { }
        public InitializePriorityAttribute(int priority) { Priority = priority; }
    }

    /// <summary>
    /// On a <c>NucleusChildCollection</c> property, makes the collection lazy: a load of the parent
    /// leaves its rows out, and the collection loads them the first time it is read. Use it for a
    /// collection that can grow large; a listing that needs the children asks for them with
    /// <c>Include(x =&gt; x.Collection)</c>, which loads them for every parent in one round trip.
    /// Refused on a [Cacheable] type, whose cached instances are shared. Has no effect on any
    /// other property.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, Inherited = false)]
    public class RetrieveOnDemandAttribute : Attribute
    { }

    /// <summary>Includes the linked entity's ID in the audit entry for this property.</summary>
    [AttributeUsage(AttributeTargets.Property, Inherited = false)]
    public class LinkWhenLoggingAttribute : Attribute
    { }

    /// <summary>Records that the property changed without recording the values.</summary>
    [AttributeUsage(AttributeTargets.Property, Inherited = false)]
    public class AbbreviatedLoggingAttribute : Attribute
    { }

    /// <summary>
    /// Omits the type or property from audit logging. Deliberately inherited: a base type that
    /// opts out of logging is making a decision about its data, and a derived type should have to
    /// opt back in explicitly rather than start logging it by default.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, Inherited = true)]
    public class SuppressLoggingAttribute : Attribute
    { }

    [AttributeUsage(AttributeTargets.Property, Inherited = false)]
    public class LinkedObjectAttribute : Attribute
    {
        public readonly string? LinkedIDProperty;
        public readonly bool ForceSave = false;

        public LinkedObjectAttribute() { }
        public LinkedObjectAttribute(bool forceSave)
        {
            ForceSave = forceSave;
        }
        public LinkedObjectAttribute(string linkedIDProperty)
        {
            LinkedIDProperty = linkedIDProperty;
        }
        public LinkedObjectAttribute(string linkedIDProperty, bool forceSave)
        {
            LinkedIDProperty = linkedIDProperty;
            ForceSave = forceSave;
        }
    }

    /// <summary>
    /// Names the key a <c>NucleusLinkedCollection&lt;TMany, TOne&gt;</c> property is kept by: the
    /// property of the many type that holds the one's ID, as in
    /// <c>[LinkedBy(nameof(Monkey.TroopId))]</c>. The key is a stored, indexed <c>Guid</c>,
    /// <c>Guid?</c>, or a <c>[LinkedObject]</c> typed as the one; the many type declares it, so its
    /// table and its queries do not depend on the one.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, Inherited = false)]
    public sealed class LinkedByAttribute : Attribute
    {
        /// <summary>The many type's key property.</summary>
        public string PropertyName { get; }

        public LinkedByAttribute(string propertyName)
        {
            ArgumentException.ThrowIfNullOrEmpty(propertyName);
            PropertyName = propertyName;
        }
    }

    [AttributeUsage(AttributeTargets.All, Inherited = false)]
    public class ParentObjectAttribute : Attribute
    {
        public readonly string? ParentIDProperty;
        public ParentObjectAttribute()
        { }
        public ParentObjectAttribute(string parentIDProperty)
        {
            ParentIDProperty = parentIDProperty;
        }
    }

}
