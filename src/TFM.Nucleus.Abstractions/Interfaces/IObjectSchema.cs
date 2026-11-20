using System.Collections;
using System.Data.Common;
using TFM.Nucleus.Abstractions.Interfaces;


namespace TFM.Nucleus
{
    

    public interface IObjectSchema
    {
        public string Name { get; }
        public string FullName { get; }
        public Type Type { get; }
        public int Priority { get; }
        public bool IsCacheable { get; }

        /// <summary>Hard cache expiry in seconds ([Cacheable] cacheLifetime); 0 = no expiry.</summary>
        public int CacheLifetime { get; }

        /// <summary>Serve-stale-while-revalidate interval in seconds ([Cacheable] refreshInterval); 0 = disabled.</summary>
        public int CacheRefreshInterval { get; }
        public bool IsDatabaseEnabled { get; }

        /// <summary>The same as <see cref="HasNucleusChildCollections"/>.</summary>
        [Obsolete("Use HasNucleusChildCollections.")]
        public bool HasLinkedChildObjects { get; }
        public bool HasParentObject { get; }
        public IPropertySchema? ParentObjectProperty { get; }
        public bool HasLinkedObjects { get; }
        public bool HasNucleusChildCollections { get; }
        public bool SuppressLogging { get; }

        /// <summary>
        /// True when the type itself carries an enabled [Deletable], so its rows can be deleted
        /// directly. A type deletable only through its owner is false here.
        /// </summary>
        public bool IsDeletable { get; }

        /// <summary>The delete behavior declared by the type's own [Deletable]; meaningful only when IsDeletable.</summary>
        public DeleteBehavior DeleteBehavior { get; }

        /// <summary>
        /// True when the type's rows can be deleted as part of their owner's graph: removed from
        /// their collection, or cascaded when an owner is deleted. Declared by [Deletable] on the
        /// type, on its collection property, or on any owner above it.
        /// </summary>
        public bool IsDeletableThroughParent => IsDeletable;

        /// <summary>
        /// How the type's rows are deleted wherever they are deleted from: the type's own
        /// [Deletable] behaviour, else the nearest declaration above it that enables deleting
        /// them, else hard. A soft behaviour gives the table the soft-delete columns.
        /// </summary>
        public DeleteBehavior EffectiveDeleteBehavior => DeleteBehavior;

        public bool Check();
        public IPropertySchema GetProperty(string propertyName);

        /// <summary>Returns null instead of throwing when the property is not part of the schema.</summary>
        public IPropertySchema? TryGetProperty(string propertyName);
        public Guid[] GetLinkedIDsForLogging(object obj);
        //public IPropertySchema? ParentProperty { get; }
        //public Guid GetParentID(object childObj);
        //public ICoreObject? GetParentObject(object childObj);
        public IEnumerable<ICoreObject> GetLinkedObjects(ICoreObject obj);

        /// <summary>The children held in the object's child collections, without loading any.</summary>
        [Obsolete("Use ICoreObject.NucleusChildCollections.")]
        public IEnumerable<ICoreObject> GetLinkedChildObjects(ICoreObject obj);
        //public IPropertySchema ParentIDProperty { get; }
        //public string ParentIDPropertyName { get; }
        public IPropertySchema[]? LinkedObjectProperties { get; }

        /// <summary>The same as <see cref="NucleusChildCollectionProperties"/>.</summary>
        [Obsolete("Use NucleusChildCollectionProperties.")]
        public IPropertySchema[]? LinkedChildObjectProperties { get; }
        public IPropertySchema[]? NucleusChildCollectionProperties { get; }
        public IObjectSchema[]? LinkedObjectSchemas { get; }

        /// <summary>The schemas of the child collections' types.</summary>
        [Obsolete("Use NucleusChildCollectionProperties and each property's ChildSchema.")]
        public IObjectSchema[]? LinkedChildObjectSchemas { get; }
        //public IPropertySchema[]? ChildObjectCollections { get; }
        //public IObjectSchema? ParentObjectSchema { get; }

        //public ISqlSchema? SqlSchema { get; }
        public IPropertySchema[] AllProperties { get; }
        public IPropertySchema[] AllDBColumns { get; }
        /// <summary>The child collection property holding <paramref name="type"/>; null when there is none.</summary>
        public IPropertySchema? TryGetChildCollectionProperty(Type type);

        /// <summary>True when the type has <c>NucleusLinkedCollection</c> properties.</summary>
        public bool HasLinkedCollections => false;

        /// <summary>The type's <c>NucleusLinkedCollection</c> properties; empty when it has none.</summary>
        public IPropertySchema[] LinkedCollectionProperties => [];

        /// <summary>
        /// The many type's key property a linked collection is loaded and kept by: the property its
        /// <c>[LinkedBy]</c> names, checked once (a stored, indexed <c>Guid</c>, <c>Guid?</c> or
        /// <c>[LinkedObject]</c> of this type). Throws for a declaration that breaks those rules.
        /// </summary>
        public IPropertySchema GetLinkedCollectionKey(IPropertySchema collection) =>
            throw new NotSupportedException($"{GetType().Name} has no linked collections.");

        public Task<IDictionary> Extract(DbDataReader reader, CancellationToken token);

        public IObjectSchema GetObjectSchema(Type type);
        public IObjectSchema GetObjectSchema<T>();
    }

    public interface IObjectSchemaCollection : IEnumerable<IObjectSchema>
    {
        public IObjectSchema this[string name] { get; }
        public IObjectSchema this[Type type] { get; }
        public bool Contains(string name);
        public bool Contains(Type type);
    }
}
