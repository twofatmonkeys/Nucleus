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
        public bool HasLinkedChildObjects { get; }
        public bool HasParentObject { get; }
        public IPropertySchema? ParentObjectProperty { get; }
        public bool HasLinkedObjects { get; }
        public bool HasNucleusChildCollections { get; }
        public bool SuppressLogging { get; }

        /// <summary>True when the type is decorated with [Deletable]; undecorated types cannot be deleted.</summary>
        public bool IsDeletable { get; }

        /// <summary>The delete behavior declared by [Deletable]; meaningful only when IsDeletable.</summary>
        public DeleteBehavior DeleteBehavior { get; }

        public bool Check();
        public IPropertySchema GetProperty(string propertyName);
        public Guid[] GetLinkedIDsForLogging(object obj);
        //public IPropertySchema? ParentProperty { get; }
        //public Guid GetParentID(object childObj);
        //public ICoreObject? GetParentObject(object childObj);
        public IEnumerable<ICoreObject> GetLinkedObjects(ICoreObject obj);
        public IEnumerable<ICoreObject> GetLinkedChildObjects(ICoreObject obj);
        //public IPropertySchema ParentIDProperty { get; }
        //public string ParentIDPropertyName { get; }
        public IPropertySchema[]? LinkedObjectProperties { get; }
        public IPropertySchema[]? LinkedChildObjectProperties { get; }
        public IPropertySchema[]? NucleusChildCollectionProperties { get; }
        public IObjectSchema[]? LinkedObjectSchemas { get; }
        public IObjectSchema[]? LinkedChildObjectSchemas { get; }
        //public IPropertySchema[]? ChildObjectCollections { get; }
        //public IObjectSchema? ParentObjectSchema { get; }

        //public ISqlSchema? SqlSchema { get; }
        public IPropertySchema[] AllProperties { get; }
        public IPropertySchema[] AllDBColumns { get; }
        public IPropertySchema? TryGetChildCollectionProperty(Type type);

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
