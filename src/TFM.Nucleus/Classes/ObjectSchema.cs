using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Reflection.Emit;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;

namespace TFM.Nucleus
{
    public static class ObjectSchema
    {
        internal static ConcurrentDictionary<Type, IObjectSchema> _schemaCache = [];
        internal static ConcurrentDictionary<Type, bool> _badCache = [];

        private static int _isInitialized; // 0 for false, 1 for true
        private static readonly object _initializeLock = new();


        /// <summary>
        /// Returns false when the type is not a Nucleus entity type. A type that is an entity but
        /// whose schema cannot be built is a configuration error rather than a "no", so it throws
        /// instead of being reported the same way as an unrelated type.
        /// </summary>
        public static bool TryGet<T>(out IObjectSchema? objectSchema) => TryGet(typeof(T), out objectSchema);

        /// <inheritdoc cref="TryGet{T}(out IObjectSchema?)"/>
        public static bool TryGet(Type type, out IObjectSchema? objectSchema)
        {
            ArgumentNullException.ThrowIfNull(type);
            if (type.IsAbstract || type.IsInterface || !type.IsSubclassOf(typeof(CoreObject)))
            {
                objectSchema = null;
                return false;
            }
            objectSchema = Get(type);
            return objectSchema != null;
        }


        public static IObjectSchema Get<T>() => Get(typeof(T));
        public static IObjectSchema Get(Type type)
        {
            if (_schemaCache.TryGetValue(type, out IObjectSchema? objSchema))
                return objSchema!;
            else if (_badCache.ContainsKey(type))
            {
                throw new ObjectSchemaException($"Type {type.Name} is not compatible with the object schema, it has been marked as bad.");
            }
            else if (type.IsAbstract || type.IsInterface)
            {
                _badCache[type] = true;
                throw new InvalidObjectTypeException(type);
            }
            else if (!type.IsSubclassOf(typeof(CoreObject)))
            {
                _badCache[type] = true;
                throw new InvalidObjectTypeException(type);
            }
            else
            {
                try
                {
                    var methInfo = typeof(ObjectSchema<>).MakeGenericType([type])
                        .GetMethod("Get", BindingFlags.Static | BindingFlags.Public, []);
                    var newObjSchema = (IObjectSchema?)methInfo?.Invoke(null, null);
                    return newObjSchema ?? throw new ObjectSchemaException("Unable to invoke generic constructor for type");
                }
                catch (TargetInvocationException tex)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tex.InnerException ?? tex).Throw();
                    throw;
                }
                catch (Exception ex)
                {
                    _badCache[type] = true;
                    throw new ObjectSchemaException($"Unable to create object schema. {ex.Message}", type, ex);
                }
            }
        }

        public static IEnumerable<IObjectSchema> GetAll()
        {
            if (Volatile.Read(ref _isInitialized) == 1)
                return _schemaCache.Values;
            return Initialize();
        }

        public static IObjectSchema[] GetWhereImplements(Type ifaceType) =>
            _schemaCache.Where(x => ifaceType.IsAssignableFrom(x.Key)).Select(x => x.Value).ToArray();

        /// <summary>
        /// Scans the assemblies beside the entry assembly and builds a schema for every concrete
        /// entity type found. Concurrent callers block on the first one rather than racing; a
        /// failed run leaves the flag clear so a later call retries.
        /// </summary>
        public static IEnumerable<IObjectSchema> Initialize()
        {
            if (Volatile.Read(ref _isInitialized) == 1)
                return _schemaCache.Values;

            lock (_initializeLock)
            {
                if (Volatile.Read(ref _isInitialized) == 1)
                    return _schemaCache.Values;

                var entryAssembly = Assembly.GetEntryAssembly()
                    ?? throw new ObjectSchemaException("There is no entry assembly to scan for entity types.");
                var appPath = Path.GetDirectoryName(entryAssembly.Location);
                if (string.IsNullOrEmpty(appPath))
                    throw new ObjectSchemaException($"Unable to determine the directory of entry assembly {entryAssembly.FullName}.");

                var assys = new List<Assembly>();
                foreach (string dll in Directory.GetFiles(appPath, "*.dll"))
                {
                    try
                    {
                        //Use .LoadFrom rather than .Load to ensure instances are current
                        assys.Add(Assembly.LoadFrom(dll));
                    }
                    catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException)
                    {
                        // Not a managed assembly, or one we cannot load in this context. Both are
                        // expected in an output folder; anything else is a real fault and propagates.
                    }
                }

                var all = ExtractNucleusTypes(assys);
                Volatile.Write(ref _isInitialized, 1);
                return all;
            }
        }

        private static IEnumerable<IObjectSchema> ExtractNucleusTypes(List<Assembly> assemblies)
        {
            List<IObjectSchema> list = [];
            var seen = new HashSet<Type>();
            foreach (var assy in assemblies)
            {
                Type?[] types;
                try
                {
                    types = assy.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // Some types in the assembly reference something unavailable. Use the ones that
                    // did load rather than discarding the whole assembly.
                    types = ex.Types;
                }
                catch (Exception ex)
                {
                    throw new ObjectSchemaException($"Error extracting entity types from {assy.FullName}. {ex.Message}");
                }

                foreach (var type in types)
                {
                    if (type is null || type.IsAbstract || type.IsInterface || !type.IsSubclassOf(typeof(CoreObject)))
                        continue;
                    if (!seen.Add(type))
                        continue;
                    if (TryGet(type, out var os))
                        list.Add(os!);
                }
            }
            return list;
        }
    }

    public class ObjectSchema<T> : IObjectSchema
    {
        public readonly Type Type;
        public readonly bool IsDatabaseEnabled;
        public readonly bool IsCacheable = false;
        public readonly int CacheLifetime = 0;
        public readonly int CacheRefreshInterval = 0;
        public readonly int Priority = 0; // Default priority, can be set by attribute
        public bool HasParentObject => _hasParentObject;
        private readonly bool _hasParentObject;
        public IPropertySchema? ParentObjectProperty => _parentObjectProperty;
        private readonly IPropertySchema? _parentObjectProperty;

        public readonly bool HasLinkedObjects;
        public readonly bool HasNucleusChildCollections;

        /// <summary>True when the type has NucleusLinkedCollection properties.</summary>
        public readonly bool HasLinkedCollections;

        public readonly bool SuppressLogging;
        public readonly bool IsDeletable;
        public readonly DeleteBehavior DeleteBehavior;
        private readonly Func<Dictionary<string, object?>, T> _ObjectBuilder;

        internal readonly Dictionary<string, PropertyInfo> dProperties = [];

        private readonly Dictionary<string, IPropertySchema> _propertyCache = [];
        private readonly IPropertySchema[] _linkedObjectProperties;
        private readonly Dictionary<Type, IPropertySchema> _nucleusChildCollectionProperties = [];
        private readonly IPropertySchema[] _linkedCollectionProperties;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IPropertySchema> _linkedCollectionKeys = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FieldInfo> _fieldCache = [];

        private static readonly FieldInfo PropertiesField =
            typeof(CoreObject).GetField("_properties", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo NewPropertiesField =
            typeof(CoreObject).GetField("_newProperties", BindingFlags.NonPublic | BindingFlags.Instance)!;


        public ObjectSchema()
        {
            throw new InvalidOperationException("ObjectSchema object may not be instantiated.");
        }
        private ObjectSchema(Type _)
        {
            Type = typeof(T);
            if (!Type.IsSubclassOf(typeof(CoreObject)))
                throw new InvalidObjectTypeException(Type);
            // TryAdd, not Add: a derived class may shadow a base member with "new", and reflection
            // returns the most derived first. First one wins; adding twice would throw.
            Type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public).ToList()
                .ForEach(f => _fieldCache.TryAdd(f.Name, f));
            PropertySchema<T>.GetAll(this).ForEach(p => _propertyCache.TryAdd(p.Name, p));
            foreach (var p in _propertyCache.Values.Where(x =>
                x.Type.IsGenericType &&
                x.Type.GetGenericTypeDefinition() == typeof(NucleusChildCollection<,>)))
            {
                // Every child lookup (the delete cascade, the parent column, change notification)
                // is keyed by the child type, so one parent holds one collection of each.
                var childType = p.Type.GetGenericArguments()[0];
                if (!_nucleusChildCollectionProperties.TryAdd(childType, p))
                    throw new ObjectSchemaException(
                        $"{Type.Name}.{_nucleusChildCollectionProperties[childType].Name} and {Type.Name}.{p.Name} both hold {childType.Name}. " +
                        "A parent can have one child collection of each child type.", Type);
            }
            HasNucleusChildCollections = _nucleusChildCollectionProperties.Count > 0;
            _linkedCollectionProperties = [.. _propertyCache.Values.Where(x => x.IsLinkedCollection)];
            HasLinkedCollections = _linkedCollectionProperties.Length > 0;
            EnsureLinkedCollectionsAreDeclarable();
            _linkedObjectProperties = [.. _propertyCache.Values.Where(x => x.IsLinkedObject)];
            _ObjectBuilder = CreateObjectBuilder();
            _parentObjectProperty = _propertyCache.Values.SingleOrDefault(x => x.IsParentObject);
            _hasParentObject = _parentObjectProperty != null;
            HasLinkedObjects = LinkedObjectProperties.Length > 0;
            IsDatabaseEnabled = !Attribute.IsDefined(Type, typeof(NotStoredAttribute)) && AllDBColumns.Length > 2;
            IsCacheable = Attribute.IsDefined(Type, typeof(CacheableAttribute));
            if (IsCacheable)
            {
                var attr = Attribute.GetCustomAttribute(Type, typeof(CacheableAttribute)) as CacheableAttribute;
                CacheLifetime = attr!.CacheLifetime;
                CacheRefreshInterval = attr.RefreshInterval;
                // A cached instance is shared by every caller, and loading a lazy collection
                // would change it under all of them.
                if (_nucleusChildCollectionProperties.Values.FirstOrDefault(x => x.IsLazyChildCollection) is { } lazy)
                    throw new ObjectSchemaException(
                        $"{Type.Name} is [Cacheable], so its child collection {lazy.Name} cannot be [RetrieveOnDemand]: " +
                        "its cached instances are shared, and loading the collection would change them for every caller.", Type);
            }
            SuppressLogging = Attribute.IsDefined(Type, typeof(SuppressLoggingAttribute));
            _deleteDeclaration = (DeletableAttribute?)Attribute.GetCustomAttribute(Type, typeof(DeletableAttribute));
            IsDeletable = _deleteDeclaration is { Enabled: true };
            DeleteBehavior = IsDeletable ? _deleteDeclaration!.Behavior : DeleteBehavior.Hard;
            _deletePolicy = new Lazy<DeletePolicy>(ResolveDeletePolicy);
            if (Attribute.IsDefined(Type, typeof(InitializePriorityAttribute)))
                Priority = ((InitializePriorityAttribute)Attribute.GetCustomAttribute(Type, typeof(InitializePriorityAttribute))!).Priority;
            foreach (var pi in AllProperties.Select(x => x.PropertyInfo))
                dProperties.TryAdd(pi.Name, pi);
        }
        /// <summary>
        /// Materializes an instance from a property dictionary: the object is created without
        /// running constructors (so <c>_isNew</c> stays false, correct for database-loaded
        /// objects) and the dictionary becomes its committed property state.
        /// Prefers an IL-emitted builder; falls back to reflection if emission fails.
        /// </summary>
        private static Func<Dictionary<string, object?>, T> CreateObjectBuilder()
        {
            try
            {
                return CreateEmitObjectBuilder();
            }
            catch (Exception)
            {
                return BuildObjectViaReflection;
            }
        }

        internal static Func<Dictionary<string, object?>, T> CreateEmitObjectBuilder()
        {
            var method = new DynamicMethod("BuildObject", typeof(T),
                [typeof(Dictionary<string, object?>)], typeof(CoreObject), skipVisibility: true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldtoken, typeof(T));
            il.Emit(OpCodes.Call, typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!);
            il.Emit(OpCodes.Call, typeof(RuntimeHelpers).GetMethod(nameof(RuntimeHelpers.GetUninitializedObject))!);
            il.Emit(OpCodes.Castclass, typeof(T));
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Stfld, PropertiesField);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Newobj, typeof(Dictionary<string, object?>).GetConstructor(Type.EmptyTypes)!);
            il.Emit(OpCodes.Stfld, NewPropertiesField);
            il.Emit(OpCodes.Ret);
            return (Func<Dictionary<string, object?>, T>)method.CreateDelegate(typeof(Func<Dictionary<string, object?>, T>));
        }

        internal static T BuildObjectViaReflection(Dictionary<string, object?> properties)
        {
            var obj = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            PropertiesField.SetValue(obj, properties);
            NewPropertiesField.SetValue(obj, new Dictionary<string, object?>());
            return obj;
        }

        internal T BuildObject(Dictionary<string, object?> properties) => _ObjectBuilder(properties);

        public string FullName => Type.FullName ?? throw new NullReferenceException();
        public string Name => Type.Name;
        /// <summary>
        /// The linked objects the object holds. A loaded link nobody has read is still only its
        /// stored ID and is left out: reading it through the property would load it.
        /// </summary>
        public IEnumerable<CoreObject> GetLinkedObjects(T thisT) =>
            thisT is CoreObject coreObj
                ? _linkedObjectProperties.Select(p => coreObj.GetRawCurrentValue(p.Name)).OfType<CoreObject>()
                : throw new InvalidCastException($"{thisT?.GetType()} is not a CoreObject, cannot get linked objects.");
        public IPropertySchema[] AllProperties => _propertyCache.Values.ToArray();
        public IPropertySchema[] AllDBColumns => _propertyCache.Values.Where(x => x.IsDBColumn).ToArray();
        public IPropertySchema[]? NucleusChildCollectionProperties => [.. _nucleusChildCollectionProperties.Values];
        public IPropertySchema[] LinkedObjectProperties => _linkedObjectProperties;
        //public IObjectSchema? ParentObjectSchema => ParentProperty?.ObjectSchema;
        // OfType filters out properties with no resolvable schema, so these never contain nulls.
        public IObjectSchema[]? LinkedObjectSchemas =>
            [.. LinkedObjectProperties.Select(x => x.ObjectSchema).OfType<IObjectSchema>()];


        public async Task<IDictionary> Extract(DbDataReader reader, CancellationToken token)
        {
            var coll = new Dictionary<Guid, T>();
            // A column named apart from its property binds to the property.
            var propertyOfColumn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in AllDBColumns)
                propertyOfColumn.TryAdd(string.IsNullOrWhiteSpace(property.DatabaseColumnAttribute?.ColumnName) ? property.Name : property.DatabaseColumnAttribute.ColumnName, property.Name);
            var columns = (await reader.GetColumnSchemaAsync(token))
                .Select(x => propertyOfColumn.GetValueOrDefault(x.ColumnName, x.ColumnName)).ToArray();
            while (await reader.ReadAsync(token))
            {
                var properties = new Dictionary<string, object?>();
                for (int i = 0; i < columns.Length; i++)
                    if (!await reader.IsDBNullAsync(i, token))
                        properties[columns[i]] = reader.GetValue(i);
                var newObj = _ObjectBuilder(properties);
                if (newObj is CoreObject coreObj)
                    coll[coreObj.ID] = newObj;
                else
                    throw new InvalidCastException($"The object created from the reader is not implemented from CoreObject.");
            }
            return coll;
        }


        public Guid[] GetLinkedIDsForLogging(T obj)
        {
            if (obj is not CoreObject coreObj)
                throw new InvalidCastException($"{typeof(T)} is not a CoreObject, cannot get linked IDs for logging.");
            var l = new List<Guid>();
            foreach (var p in _propertyCache.Values.Where(x => x.LinkWhenLogging))
            {
                var v = coreObj.GetRawCurrentValue(p.Name);
                if (v is Guid guid)
                    l.Add(guid);
                else if (v is ICoreObject linked)
                    l.Add(linked.ID);
            }
            return l.ToArray();
        }

        Type IObjectSchema.Type => Type;
        int IObjectSchema.Priority => Priority;
        bool IObjectSchema.IsCacheable => IsCacheable;
        bool IObjectSchema.IsDatabaseEnabled => IsDatabaseEnabled;
        bool IObjectSchema.HasParentObject => HasParentObject;
        bool IObjectSchema.HasLinkedObjects => HasLinkedObjects;
        bool IObjectSchema.HasNucleusChildCollections => HasNucleusChildCollections;
        bool IObjectSchema.SuppressLogging => SuppressLogging;
        int IObjectSchema.CacheLifetime => CacheLifetime;
        int IObjectSchema.CacheRefreshInterval => CacheRefreshInterval;
        bool IObjectSchema.IsDeletable => IsDeletable;
        DeleteBehavior IObjectSchema.DeleteBehavior => DeleteBehavior;
        bool IObjectSchema.IsDeletableThroughParent => IsDeletableThroughParent;
        DeleteBehavior IObjectSchema.EffectiveDeleteBehavior => EffectiveDeleteBehavior;
        // Held links only: a loaded link nobody has read is its stored ID, and is not loaded here.
        IEnumerable<ICoreObject> IObjectSchema.GetLinkedObjects(ICoreObject obj) =>
            _linkedObjectProperties.Select(p => obj.GetPropertyValue(p.Name)).OfType<ICoreObject>();
        Guid[] IObjectSchema.GetLinkedIDsForLogging(object obj) => GetLinkedIDsForLogging((T)obj);
        IPropertySchema[]? IObjectSchema.NucleusChildCollectionProperties => NucleusChildCollectionProperties;
        IObjectSchema[]? IObjectSchema.LinkedObjectSchemas => LinkedObjectSchemas;
        IObjectSchema IObjectSchema.GetObjectSchema(Type type) => ObjectSchema.Get(type);
        IObjectSchema IObjectSchema.GetObjectSchema<TAlt>() => ObjectSchema.Get<TAlt>();

        public object? GetDBValueFromProperty(T obj, string propName) => 
            (obj is CoreObject coreObj) ? GetProperty(propName).GetDBValue(coreObj) : throw new InvalidCastException("Type is not CoreObject");


        public IPropertySchema GetProperty(string propName)
        {
            if (_propertyCache.TryGetValue(propName, out var p))
                return p;
            else
                throw new PropertyNotFoundException(propName, this);
        }
        public IPropertySchema? TryGetProperty(string propName)
        {
            if (propName.IsNullOrEmpty())
                return null;
            if (_propertyCache.TryGetValue(propName, out var p))
                return p;
            else
                return null;
        }
        public IPropertySchema[] GetChangedProperties(T obj)
        {
            var l = new List<IPropertySchema>();
            if (obj is not CoreObject coreObj)
                throw new InvalidOperationException("The object must be a CoreObject to get changed properties.");
            foreach (var pColl in coreObj.ChangedProperties)
                l.Add(GetProperty(pColl.Key));
            return [.. l];
        }
        public IPropertySchema[] GetOverriddenProperties()
        {
            var l = new List<IPropertySchema>();
            foreach (var p in _propertyCache.Where(x => x.Value.IsDBColumn && x.Value.IsOverridden))
                l.Add(p.Value);
            return l.ToArray();
        }
        public IPropertySchema? TryGetChildCollectionProperty(Type type) => _nucleusChildCollectionProperties.GetValueOrDefault(type);

        #region Linked collections

        /// <summary>The type's NucleusLinkedCollection properties, built once.</summary>
        public IPropertySchema[] LinkedCollectionProperties => _linkedCollectionProperties;

        bool IObjectSchema.HasLinkedCollections => HasLinkedCollections;
        IPropertySchema[] IObjectSchema.LinkedCollectionProperties => _linkedCollectionProperties;

        /// <summary>
        /// The rules a linked collection can be checked against from this type alone, at schema
        /// build: the collection is this type's own, two collections never share a key, and the
        /// type is a root that is not [Cacheable].
        /// </summary>
        private void EnsureLinkedCollectionsAreDeclarable()
        {
            if (!HasLinkedCollections)
                return;
            var first = _linkedCollectionProperties[0];
            // A child is deleted by its parent's cascade, whose rows nobody loaded; refusing to
            // delete a one with members cannot be checked there, so a one is a root.
            if (_propertyCache.Values.Any(x => x.IsParentObject))
                throw new ObjectSchemaException(
                    $"{Type.Name}.{first.Name} is a linked collection, but {Type.Name} is a child type. " +
                    "The one side of a linked collection is a root, which is deleted only on its own.", Type);
            // A cached instance is shared by every caller, and loading its members would change it
            // under all of them, as for a lazy child collection.
            if (Attribute.IsDefined(Type, typeof(CacheableAttribute)))
                throw new ObjectSchemaException(
                    $"{Type.Name} is [Cacheable], so it cannot have the linked collection {first.Name}: " +
                    "its cached instances are shared, and loading the members would change them for every caller.", Type);
            foreach (var collection in _linkedCollectionProperties)
            {
                var owner = collection.Type.GetInterfaces()
                    .First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(TFM.Nucleus.Abstractions.Interfaces.INucleusLinkedCollection<,>))
                    .GetGenericArguments()[1];
                if (owner != Type)
                    throw new ObjectSchemaException(
                        $"{Type.Name}.{collection.Name} is a linked collection of {owner.Name}. Declare it as NucleusLinkedCollection<{collection.LinkedType!.Name}, {Type.Name}>.", Type);
            }
            var shared = _linkedCollectionProperties
                .GroupBy(p => (p.LinkedType, p.LinkedByPropertyName))
                .FirstOrDefault(g => g.Count() > 1);
            if (shared is not null)
                throw new ObjectSchemaException(
                    $"{string.Join(" and ", shared.Select(p => $"{Type.Name}.{p.Name}"))} are both kept by {shared.Key.LinkedType!.Name}.{shared.Key.LinkedByPropertyName}. " +
                    "One key holds one relationship; give each collection a key of its own.", Type);
        }

        /// <inheritdoc cref="IObjectSchema.GetLinkedCollectionKey"/>
        public IPropertySchema GetLinkedCollectionKey(IPropertySchema collection)
        {
            ArgumentNullException.ThrowIfNull(collection);
            if (!collection.IsLinkedCollection || !_linkedCollectionProperties.Contains(collection))
                throw new ArgumentException($"{collection.Name} is not a linked collection of {Type.Name}.", nameof(collection));
            return _linkedCollectionKeys.GetOrAdd(collection.Name, static (_, args) => args.Self.ResolveLinkedCollectionKey(args.Collection), (Self: this, Collection: collection));
        }

        /// <summary>
        /// The key a linked collection names, checked against the many type: resolved on first use
        /// and forced when the one's table is built, since the many type's schema cannot be read
        /// while this one's is being built (the many may have a collection of this type).
        /// </summary>
        private IPropertySchema ResolveLinkedCollectionKey(IPropertySchema collection)
        {
            var manyType = collection.LinkedType!;
            var where = $"{Type.Name}.{collection.Name}";
            var many = ObjectSchema.Get(manyType);
            if (many.HasParentObject)
                throw new ObjectSchemaException(
                    $"{where} is a linked collection of {manyType.Name}, which is a child type. Rows a parent owns are a NucleusChildCollection on the parent.", Type);
            var name = collection.LinkedByPropertyName!;
            var key = many.TryGetProperty(name)
                ?? throw new ObjectSchemaException($"{where} is kept by {manyType.Name}.{name}, which {manyType.Name} does not have.", Type);
            if (!key.IsDBColumn)
                throw new ObjectSchemaException(
                    $"{where} is kept by {manyType.Name}.{name}, which is not a stored column. Mark it [DatabaseColumn(Indexed = true)].", Type);
            var holdsId = key.Type == typeof(Guid) || key.Type == typeof(Guid?);
            var holdsOne = key.IsLinkedObject && key.Type.IsAssignableFrom(Type);
            if (!holdsId && !holdsOne)
                throw new ObjectSchemaException(
                    key.Type.IsAssignableFrom(Type)
                        ? $"{where} is kept by {manyType.Name}.{name}, which holds a {Type.Name} but is not a [LinkedObject]. Mark it [LinkedObject]."
                        : $"{where} is kept by {manyType.Name}.{name}, a {key.Type.Name}. The key holds the {Type.Name}'s ID: a Guid, a Guid? or a [LinkedObject] typed as {Type.Name}.",
                    Type);
            // Every load of the collection, every include and every refused delete filters on it.
            if (!key.IsIndexed && !key.IsUnique)
                throw new ObjectSchemaException(
                    $"{where} is kept by {manyType.Name}.{name}, which is not indexed, so every load of the collection would scan {manyType.Name}'s table. " +
                    "Mark it [DatabaseColumn(Indexed = true)].", Type);
            return key;
        }

        #endregion
        public PropertyInfo? GetChildCollectionProperty(Type type) => TryGetChildCollectionProperty(type)?.PropertyInfo;

        public static ObjectSchema<T> Get()
        {
            if (ObjectSchema._schemaCache.TryGetValue(typeof(T), out var objSchema))
                return (ObjectSchema<T>)objSchema!;
            else
                return TryAdd();
        }
        private static ObjectSchema<T> TryAdd()
        {
            try
            {
                var o = new ObjectSchema<T>(typeof(T));
                ObjectSchema._schemaCache[typeof(T)] = o;
                return o;
            }
            catch (Exception ex)
            {
                throw new ObjectSchemaException($"Unable to build object schema. {ex.Message}", typeof(T), ex);
            }
        }



        public bool Check()
        {
            try
            {
                _ = _deletePolicy.Value; // a conflicting delete declaration fails here, not at the first delete
                foreach (var collection in _linkedCollectionProperties)
                    _ = GetLinkedCollectionKey(collection);
                if (HasNucleusChildCollections)
                {
                    foreach (var c in _nucleusChildCollectionProperties.Values)
                    {
                        if (!c.CanRead && !c.CanWrite)
                            throw new ObjectSchemaException($"The property '{c.Name}' has an invalid specification, it must be readable.", Type);
                        if (c.Type.GetGenericArguments().Length != 2)
                            throw new ObjectSchemaException($"The property '{c.Name}' is an invalid enumerable for a child object.", Type);
                    }
                }
                return true;
            }
            catch (ObjectSchemaException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ObjectSchemaException($"Object schema is invalid. {ex.Message}", Type, ex);
            }
        }

        #region Delete policy

        private readonly DeletableAttribute? _deleteDeclaration;
        private readonly Lazy<DeletePolicy> _deletePolicy;

        /// <summary>
        /// True when the type's rows can be deleted as part of their owner's graph: removed from
        /// their collection or cascaded with an owner. See <see cref="DeletableAttribute"/>.
        /// </summary>
        public bool IsDeletableThroughParent => _deletePolicy.Value.ThroughParent;

        /// <summary>
        /// The behaviour the type's rows are deleted with: the type's own [Deletable], else the
        /// nearest declaration above it that enables deleting them, else hard.
        /// </summary>
        public DeleteBehavior EffectiveDeleteBehavior => _deletePolicy.Value.Behavior;

        private readonly record struct DeletePolicy(bool ThroughParent, DeleteBehavior Behavior);

        /// <summary>The schema of the type that owns this one's rows, or null for a root.</summary>
        private IObjectSchema? Owner()
        {
            var ownerType = _parentObjectProperty?.Type;
            return ownerType is null || ownerType.IsAbstract || !ownerType.IsSubclassOf(typeof(CoreObject))
                ? null
                : ObjectSchema.Get(ownerType);
        }

        // The owner chain is walked through each owner's own policy; a type reached again while
        // it is being resolved is its own owner, which no delete could finish.
        [ThreadStatic]
        private static HashSet<Type>? _resolving;

        /// <summary>
        /// Works out the policy from the type's own declaration and those above it. A child type
        /// has one owner type (<c>CoreChildObject&lt;T, TParent&gt;</c>) and its owner one
        /// collection of it, so the chain is fixed and the policy is resolved once. Throws when
        /// the type forbids deletion and a declaration above it would delete its rows.
        /// </summary>
        private DeletePolicy ResolveDeletePolicy()
        {
            _resolving ??= [];
            if (!_resolving.Add(Type))
                throw new ObjectSchemaException($"{Type.Name} is its own owner through its parent chain, so no delete of it could finish.", Type);
            try
            {
                var (enabledAbove, behaviorAbove, source) = ResolveAbove();
                if (_deleteDeclaration is { Enabled: false } && enabledAbove)
                    throw new ObjectSchemaException(
                        $"{Type.Name} is [Deletable(false)], but {source} makes its rows deletable through their owner. " +
                        $"Deleting the owner, or removing a {Type.Name} from its collection, would have to delete rows the type forbids deleting. " +
                        "Remove one of the two declarations.", Type);
                var policy = IsDeletable ? new DeletePolicy(true, DeleteBehavior)
                    : enabledAbove ? new DeletePolicy(true, behaviorAbove)
                    : new DeletePolicy(false, DeleteBehavior.Hard);
                if (policy.Behavior == DeleteBehavior.Soft && Owner() is { IsDeletableThroughParent: true, EffectiveDeleteBehavior: DeleteBehavior.Hard } owner)
                    throw new ObjectSchemaException(
                        $"{Type.Name} rows are soft deleted, but its owner {owner.Name} is hard deleted. A delete of a {owner.Name} removes its row " +
                        $"and keeps the flagged {Type.Name} rows, which would point at a row that no longer exists. " +
                        $"Make {owner.Name} soft deleted too, or delete {Type.Name} rows hard.", Type);
                return policy;
            }
            finally
            {
                _resolving.Remove(Type);
            }
        }

        /// <summary>
        /// Whether a declaration above the type enables deleting its rows (its collection
        /// property, its owner, or anything above the owner), the behaviour the nearest one gives,
        /// and a description of it for messages.
        /// </summary>
        private (bool Enabled, DeleteBehavior Behavior, string Source) ResolveAbove()
        {
            if (Owner() is not { } owner)
                return (false, DeleteBehavior.Hard, "");
            var collection = owner.TryGetChildCollectionProperty(Type);
            if (collection is null)
                return (false, DeleteBehavior.Hard, "");
            if (collection.DeleteDeclaration is { Enabled: false })
                throw new ObjectSchemaException(
                    $"[Deletable(false)] on {owner.Name}.{collection.Name} is not supported. Removals from a collection delete only when declared, " +
                    $"so leave the property undeclared, or declare [Deletable(false)] on {Type.Name} to forbid deleting its rows anywhere.", Type);
            var ownerInGraph = owner.IsDeletableThroughParent;
            if (collection.DeleteDeclaration is { Enabled: true } declared)
            {
                var behavior = declared.IsBehaviorDeclared ? declared.Behavior
                    : ownerInGraph ? owner.EffectiveDeleteBehavior
                    : DeleteBehavior.Hard;
                return (true, behavior, $"[Deletable] on {owner.Name}.{collection.Name}");
            }
            if (ownerInGraph)
                return (true, owner.EffectiveDeleteBehavior,
                    owner.IsDeletable ? $"[Deletable] on {owner.Name}" : $"a [Deletable] declaration above {owner.Name}");
            return (false, DeleteBehavior.Hard, "");
        }

        #endregion

        public override string ToString() => $"ObjectSchema({FullName ?? "null"})";
    }

    public class ObjectSchemaException : TFM.Nucleus.NucleusException
    {
        public Type? Type { get; }

        public ObjectSchemaException(string message, Type? type = null, Exception? innerException = null)
            : base(BuildMessage(message, type), innerException)
        {
            Type = type;
        }

        private static string BuildMessage(string message, Type? type) =>
            type != null
                ? $"The type {type.Name} is not compatible with the object schema, {message}"
                : $"Object Schema error for Null Type, {message}";
    }

    public class InvalidObjectTypeException : TFM.Nucleus.NucleusException
    {
        public Type? Type { get; }

        public InvalidObjectTypeException(string typeName, Exception? innerException = null)
            : base($"The type {typeName} is not compatible with the object schema, Invalid Type", innerException)
        { }

        public InvalidObjectTypeException(Type type, Exception? innerException = null)
            : base($"The type {type.Name} is not compatible with the object schema, Invalid Type", innerException)
        {
            Type = type;
        }
    }

    public class PropertyNotFoundException : TFM.Nucleus.NucleusException
    {
        public string PropertyName { get; }
        public IObjectSchema ObjectSchema { get; }

        public PropertyNotFoundException(string propName, IObjectSchema objectSchema)
            : base($"The property {propName} was not found in {objectSchema}'s object schema")
        {
            PropertyName = propName;
            ObjectSchema = objectSchema;
        }
    }

}
