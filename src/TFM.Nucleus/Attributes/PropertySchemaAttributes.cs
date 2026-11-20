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

    /// <summary>
    /// On a <c>NucleusChildCollection</c> property, makes the collection lazy: a load of the parent
    /// leaves its rows out, and the collection loads them the first time it is read. Use it for a
    /// collection that can grow large; a listing that needs the children asks for them with
    /// <c>Include(x =&gt; x.Collection)</c>, which loads them for every parent in one round trip.
    /// Refused on a [Cacheable] type, whose cached instances are shared. Has no effect on any
    /// other property.
    /// </summary>
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

    /// <summary>
    /// Names the key a <c>NucleusLinkedCollection&lt;TMany, TOne&gt;</c> property is kept by: the
    /// property of the many type that holds the one's ID, as in
    /// <c>[LinkedBy(nameof(Voucher.CampaignId))]</c>. The key is a stored, indexed <c>Guid</c>,
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

    /// <summary>
    /// The v1 child collection marker. It has no effect on load, save or delete: a child
    /// collection is a <c>NucleusChildCollection&lt;TChild, TParent&gt;</c> property, found by its type.
    /// </summary>
    [Obsolete("Has no effect. Declare the property as NucleusChildCollection<TChild, TParent>; it is found by its type.")]
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
