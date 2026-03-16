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

        private static int IsInitialised = 0; // 0 for false, 1 for true
        private static int IsInitialising = 0; // 0 for false, 1 for true    


        public static bool TryGet<T>(out IObjectSchema? objectSchema) => TryGet(typeof(T), out objectSchema);
        public static bool TryGet(Type type, out IObjectSchema? objectSchema)
        {
            try
            { 
                objectSchema = Get(type);
                return objectSchema != null;
            }
            catch (Exception)
            {
                objectSchema = null;
                return false;
            }
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
                    throw new ObjectSchemaException($"Unable to create object schema for type {type.Name}. {ex.Message}", type);
                }
            }
        }

        public static IEnumerable<IObjectSchema> GetAll()
        {
            if(Interlocked.Equals(IsInitialised, 0))
                return Initialise();
            else
                return _schemaCache.Values;
        }

        public static IObjectSchema[] GetWhereImplements(Type ifaceType) =>
            _schemaCache.Where(x => ifaceType.IsAssignableFrom(x.Key)).Select(x => x.Value).ToArray();

        public static IEnumerable<IObjectSchema> Initialise()
        {
            if(Interlocked.Equals(IsInitialised, 1))
                return _schemaCache.Values; // Already initialised, return cached values

            if (Interlocked.CompareExchange(ref IsInitialising, 1, 0) == 1)
            { 
                var sw = new System.Diagnostics.Stopwatch();
                sw.Start();
                while (Interlocked.Equals(IsInitialising, 1))
                {
                    Thread.Sleep(100);
                    if(sw.ElapsedMilliseconds > 30000) // Wait for a maximum of 30 seconds
                    {
                        Interlocked.Exchange(ref IsInitialising, 0); // Reset initialising flag
                        throw new Exception("Object schema initialisation is taking too long, aborting.");
                    }
                }
                if (Interlocked.Equals(IsInitialised, 1))
                    return _schemaCache.Values;
                throw new Exception("There was an error when initialising the object schema, aborting.");
            }
            try
            {
                var assys = new List<Assembly>();
                var types = new List<Type>();
                var appPath = Path.GetDirectoryName(Assembly.GetEntryAssembly()!.Location) ?? "Error";

                foreach (string dll in Directory.GetFiles(appPath, "*.dll"))
                {
                    try
                    {
                        //Use .LoadFrom rather than .Load to ensure instances are current
                        assys.Add(Assembly.LoadFrom(dll));
                    }
                    catch (Exception)
                    {

                    }
                }
                var all = ExtractNucleusTypes(assys);
                Interlocked.Exchange(ref IsInitialised, 1); // Set IsInitialised to true
                Interlocked.Exchange(ref IsInitialising, 0); // Set initialising to false
                return all;
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref IsInitialised, 0); // Set IsInitialised to false
                Interlocked.Exchange(ref IsInitialising, 0); // Set initialising to false
                throw new Exception($"Error loading assemblies from base directory. {ex.Message}", ex);
            }
        }

        private static IEnumerable<IObjectSchema> ExtractNucleusTypes(List<Assembly> assemblies)
        {
            List<IObjectSchema> list = [];
            foreach (var assy in assemblies)
            {
                try
                {
                    foreach (var type in assy.GetTypes())
                        if (type.IsSubclassOf(typeof(CoreObject)))
                            if (!list.Any(x => x.Type == type))
                            {
                                //if (Attribute.IsDefined(type, typeof(InitializePriority)))
                                //    priority = ((InitializePriority)Attribute.GetCustomAttribute(type, typeof(InitializePriority))!).Priority;
                                if(TryGet(type, out var os))
                                    list.Add(os!);
                            }
                }
                catch (Exception)
                {
                    throw new Exception($"Error Extracting TFM types from {assy.FullName}");
                    //Error.WriteExceptionToFile(new Exception($"Error Extracting TFM types from {assy.FullName}",ex));
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
        public readonly bool HasLinkedChildObjects;
        public readonly bool HasLinkedParentObject;
        public readonly bool HasLinkedObjects;
        public readonly bool SuppressLogging;
        private readonly Func<Dictionary<string, object>, T> _ObjectBuilder;
        private readonly Func<T> _Creator;

        //private readonly Func<T, string, object> _rawGetter;
        //private readonly Action<string, object> _rawSetter;

        public readonly IPropertySchema? ParentProperty;

        internal readonly Dictionary<string, PropertyInfo> dProperties = [];
        internal readonly Func<T, string, object>? _Getter;
        internal readonly Action<string, object>? _Setter;

        private readonly Dictionary<string, IPropertySchema> _propertyCache = [];
        private readonly Dictionary<Type, IPropertySchema> _linkedChildObjectProperties = [];
        private readonly Dictionary<Type, IPropertySchema> _linkedObjectProperties = [];
        private readonly Dictionary<string, FieldInfo> _fieldCache = [];

        //public bool HasLinkedChildCollections => _linkedChildCollections.Count > 0;


        public ObjectSchema()
        {
            throw new InvalidOperationException("ObjectSchema object may not be instantiated.");
        }
        private ObjectSchema(Type _)
        {
            Type = typeof(T);
            if (!Type.IsSubclassOf(typeof(CoreObject)))
                throw new InvalidObjectTypeException(Type);
            Type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public).ToList()
                .ForEach(f => _fieldCache.Add(f.Name, f));
            PropertySchema<T>.GetAll(this).ForEach(p => _propertyCache.Add(p.Name, p));
            foreach (var p in _propertyCache.Values.Where(x => x.IsLinkedChildCollection))
                _linkedChildObjectProperties.Add(p.ChildType ?? throw new NullReferenceException(), p);
            //if (HasDefaultConstructor())
            //    _Creator = Expression.Lambda<Func<T>>(Expression.New(Type)).Compile();
            //else
            _Creator = () => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            var meth = new DynamicMethod(string.Empty, typeof(void), [typeof(T), typeof(Dictionary<string, object>)]);
            var il = meth.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, typeof(T).BaseType?.BaseType?.GetField("_properties", BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new NullReferenceException());
            il.Emit(OpCodes.Ret);
            _ObjectBuilder = _CreateObjectBuilder();
            //_rawGetter = (Func<T, string, object>)Delegate.CreateDelegate(Expression.GetDelegateType(new Type[] { typeof(T), typeof(string), typeof(object) }), null,
            //    Type.GetMethod(nameof(CoreObject.GetRawCurrentValue), BindingFlags.Instance | BindingFlags.NonPublic));
            //_rawSetter = (Action< string, object>)Delegate.CreateDelegate(Expression.GetDelegateType(new Type[] {  typeof(string), typeof(object), typeof(void) }), null,
            //    Type.GetMethod(nameof(CoreObject.SetRawCurrentValue), BindingFlags.Instance | BindingFlags.NonPublic));
            //_Getter = (Func<T, string, object>)Delegate.CreateDelegate(Expression.GetDelegateType(new Type[] { typeof(T), typeof(string), typeof(object) }), null,
            //    Type.GetMethod(nameof(CoreObject.GetValue), BindingFlags.Instance | BindingFlags.NonPublic));
            //_Setter = (Action<string, object>)Delegate.CreateDelegate(Expression.GetDelegateType(new Type[] { typeof(string), typeof(object), typeof(void) }), null,
            //    Type.GetMethod(nameof(CoreObject.SetValue), BindingFlags.Instance | BindingFlags.NonPublic));
            ParentProperty = _propertyCache.Values.SingleOrDefault(x => x.IsParentObject);
            HasLinkedParentObject = ParentProperty != null;
            HasLinkedObjects = LinkedObjectProperties.Length > 0;
            HasLinkedChildObjects = _linkedChildObjectProperties.Count > 0;
            IsDatabaseEnabled = !Attribute.IsDefined(Type, typeof(SuppressDB)) && AllDBColumns.Length > 2;
            IsCacheable = Attribute.IsDefined(Type, typeof(Cacheable));
            if (IsCacheable)
            {
                var attr = Attribute.GetCustomAttribute(Type, typeof(Cacheable)) as Cacheable;
                CacheLifetime = attr!.CacheLifetime;
                CacheRefreshInterval = attr.RefreshInterval;
            }
            SuppressLogging = Attribute.IsDefined(Type, typeof(SuppressLogging));
            if (Attribute.IsDefined(Type, typeof(InitializePriority)))
                Priority = ((InitializePriority)Attribute.GetCustomAttribute(Type, typeof(InitializePriority))!).Priority;
            foreach (var pi in AllProperties.Select(x => x.PropertyInfo))
                dProperties.Add(pi.Name, pi);
        }
        private bool HasDefaultConstructor() => Type.IsValueType || Type.GetConstructor(Type.EmptyTypes) != null;

        private Func<Dictionary<string, object>, T> _CreateObjectBuilder()
        {
            var meth = new DynamicMethod("BuildObject", typeof(T), new Type[] { typeof(T), typeof(Dictionary<string, object>) });
            var il = meth.GetILGenerator();
            var loc_T = il.DeclareLocal(typeof(T));
            il.Emit(OpCodes.Ldtoken, typeof(T));
            il.Emit(OpCodes.Call, typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!);
            il.Emit(OpCodes.Call, typeof(RuntimeHelpers).GetMethod(nameof(RuntimeHelpers.GetUninitializedObject))!);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Stloc, loc_T);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, typeof(CoreObject).GetField("_properties", BindingFlags.NonPublic | BindingFlags.Instance)!);
            il.Emit(OpCodes.Ldloc, loc_T);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stfld, typeof(CoreObject).GetField("_isReadOnly", BindingFlags.NonPublic | BindingFlags.Instance)!);
            il.Emit(OpCodes.Ldloc, loc_T);
            il.Emit(OpCodes.Newobj, typeof(Dictionary<string, object>).GetConstructors()[0]);
            il.Emit(OpCodes.Stfld, typeof(CoreObject).GetField("_newProperties", BindingFlags.NonPublic | BindingFlags.Instance)!);
            il.Emit(OpCodes.Ldloc, loc_T);
            il.Emit(OpCodes.Ret);
            return (Func<Dictionary<string, object>, T>)meth.CreateDelegate(typeof(Func<Dictionary<string, object>, T>));
        }

        public string FullName => Type.FullName ?? throw new NullReferenceException();
        public string Name => Type.Name;
        //public IPropertySchema ParentIDProperty => TryGetProperty($"{ParentIDPropertyName}");
        public Guid GetParentID(T thisT) => GetParentObject(thisT)?.ID ?? Guid.Empty;
        public CoreObject? GetParentObject(T thisT) => 
            (thisT is CoreObject coreObj) ? (CoreObject?)(HasLinkedParentObject ? ParentProperty?.GetValue(coreObj) : null) :
            throw new InvalidCastException($"{thisT?.GetType()} is not a CoreObject, cannot get parent object.");
        public IEnumerable<CoreObject> GetLinkedObjects(T thisT) => throw new NotImplementedException(); //LinkedObjectProperties.Select(x => x.GetValue(thisT)).OfType<CoreObject>();
        //public SqlSchema<T> SqlSchema => IsDatabaseEnabled ? SqlSchema<T>.Get() : null;
        public IPropertySchema[] AllProperties => _propertyCache.Values.ToArray();
        public IPropertySchema[] AllDBColumns => _propertyCache.Values.Where(x => x.IsDBColumn).ToArray();
        public IPropertySchema[] LinkedChildObjectProperties => [.. _linkedChildObjectProperties.Values];
        public IPropertySchema[] LinkedObjectProperties => [.. _linkedObjectProperties.Values];
        public IObjectSchema? ParentObjectSchema => ParentProperty?.ObjectSchema;
        public IObjectSchema[]? LinkedObjectSchemas => [.. LinkedObjectProperties.Select(x => x.ObjectSchema)];
        public IObjectSchema[]? LinkedChildObjectSchemas => [.. LinkedChildObjectProperties.Select(x => x.ChildSchema)];


        public async Task<IDictionary> Extract(DbDataReader reader, CancellationToken token)
        {
            try
            {
                var coll = new Dictionary<Guid, T>();
                var columns = (await reader.GetColumnSchemaAsync()).Select(x => x.ColumnName).ToArray();
                while (await reader.ReadAsync(token))
                {
                    var properties = new Dictionary<string, object>();
                    for (int i = 0; i < columns.Length; i++)
                        if (!await reader.IsDBNullAsync(i))
                            properties[columns[i]] = reader.GetValue(i);
                    var newObj = _ObjectBuilder(properties);
                    if (newObj is CoreObject coreObj)
                        coll[coreObj.ID] = newObj;
                    else
                        throw new InvalidCastException($"The object created from the reader is not implemented from CoreObject.");
                    
                }
                return coll;
            }
            catch (Exception)
            {
                throw;
            }
        }


        public Guid[] GetLinkedIDsForLogging(T obj)
        {
            var l = new List<Guid>();
            foreach (var p in _propertyCache.Values.Where(x => x.LinkWhenLogging))
            {
                var v = (obj as CoreObject).GetRawCurrentValue(p.Name);
                if (v != null)
                {
                    if (v.GetType() == typeof(Guid))
                        l.Add((Guid)v);
                    else
                        l.Add(((ICoreObject)v).ID);
                }
            }
            return l.ToArray();
        }

        Type IObjectSchema.Type => Type;
        int IObjectSchema.Priority => Priority;
        bool IObjectSchema.IsCacheable => IsCacheable;
        bool IObjectSchema.IsDatabaseEnabled => IsDatabaseEnabled;
        bool IObjectSchema.HasLinkedChildObjects => HasLinkedChildObjects;
        bool IObjectSchema.HasLinkedParentObject => HasLinkedParentObject;
        bool IObjectSchema.HasLinkedObjects => HasLinkedObjects;
        bool IObjectSchema.SuppressLogging => SuppressLogging;
        Guid IObjectSchema.GetParentID(object childObj) => GetParentID((T)childObj);
        ICoreObject? IObjectSchema.GetParentObject(object childObj) => GetParentObject((T)childObj);
        IEnumerable<ICoreObject> IObjectSchema.GetLinkedObjects(ICoreObject obj) => throw new NotImplementedException();
        IEnumerable<ICoreObject> IObjectSchema.GetLinkedChildObjects(ICoreObject obj) => throw new NotImplementedException();
        Guid[] IObjectSchema.GetLinkedIDsForLogging(object obj) => GetLinkedIDsForLogging((T)obj);
        IPropertySchema? IObjectSchema.ParentProperty => ParentProperty;
        //IPropertySchema IObjectSchema.ParentIDProperty => ParentIDProperty;
        IPropertySchema[] IObjectSchema.LinkedChildObjectProperties => LinkedChildObjectProperties;
        IObjectSchema? IObjectSchema.ParentObjectSchema => ParentObjectSchema;
        IObjectSchema[]? IObjectSchema.LinkedObjectSchemas => LinkedObjectSchemas;
        IObjectSchema[]? IObjectSchema.LinkedChildObjectSchemas => LinkedChildObjectSchemas;

        //ISqlSchema IObjectSchema.SqlSchema => throw new NotImplementedException();

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
        //public CoreObject[] GetChangedLinkedObjects(T obj)
        //{
        //    return obj.;
        //}
        public IPropertySchema? TryGetChildCollectionProperty(Type type) => _linkedChildObjectProperties.GetValueOrDefault(type);
        public PropertyInfo? GetChildCollectionProperty(Type type)
        {
            foreach (var p in LinkedChildObjectProperties)
                if (p.ChildType == type)
                    return p.PropertyInfo;
            return null;
        }
        //public (ObjectSchema ObjectSchema, PropertyInfo BoundPropertyInfo)[] ChildCollectionProperties
        //{
        //    get
        //    {
        //        var l = new List<(ObjectSchema, PropertyInfo)>();
        //        foreach (var pi in Properties)
        //            if (Attribute.IsDefined(pi, typeof(LinkedChildCollection)))
        //                l.Add((Get(pi.PropertyType.GetGenericArguments()[0]), pi));
        //        return l.ToArray();
        //    }
        //}

        //public PropertySchema ParentIDProperty => TryGetProperty(ParentIDPropertyName);

        //public string ParentIDPropertyName
        //{
        //    get
        //    {
        //        var attr = (ParentObject)ParentProperty.GetCustomAttribute(typeof(ParentObject));
        //        var parentIDPropertyName = $"{ParentProperty.Name}ID";
        //        if (!attr.ParentGuidProperty.IsNullOrEmpty())
        //            parentIDPropertyName = attr.ParentGuidProperty;
        //        return parentIDPropertyName;
        //    }
        //}

        //public static ObjectSchema<T> Get(T _) => Get();
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
                throw new Exception($"Unable to build object schema for type{typeof(T)}. {ex.Message}");
            }
        }



        public bool Check()
        {
            try
            {
                if (HasLinkedChildObjects)
                {
                    foreach (var c in LinkedChildObjectProperties)
                    {
                        if (!c.CanRead && !c.CanWrite)
                            throw new Exception($"The Property '{c.Name}' in the object '{FullName}' has an invalid specification, it must be Readonly.");
                        //if (c.ChildSchema.ParentIDPropertyName.IsNullOrEmpty())
                        //    throw new Exception($"The Parent ID property in the child object '{c.ChildSchema}' is either missing, not or incorrectly defined in the ParentProperty Attribute's Property 'ParentGuidProperty'.");
                        //if (c.ChildSchema.ParentIDProperty?.Type != typeof(Guid))
                        //    throw new Exception($"The Property '{c.ChildSchema.ParentIDProperty}' in the child object '{c.ChildSchema}' is an invalid Type({c.ChildSchema.ParentIDProperty?.Type}), it should be a 'System.Guid'.");
                        //if (!c.ChildSchema.ParentIDProperty.IsDBColumn)
                        //    throw new Exception($"The Property '{c.ChildSchema.ParentIDProperty}' in the child object '{c.ChildSchema}' is missing the 'DBColumn' Attribute.");
                        //if (!c.ChildSchema.ParentIDProperty.CanRead || !c.ChildSchema.ParentIDProperty.CanWrite)
                        //    throw new Exception($"The Property '{c.ChildSchema.ParentIDProperty}' in the child object '{c.ChildSchema}' has an invalid specification, it must be ReadWrite.");
                        if (!c.ChildSchema!.ParentProperty!.CanRead || !c.ChildSchema.ParentProperty.CanWrite)
                            throw new Exception($"The Property '{c.ChildSchema.ParentProperty}' in the child object '{c.ChildSchema}' has an invalid specification, it must be ReadWrite.");

                        if (c.Type.GetGenericArguments().Length != 2)
                            throw new Exception("Invalid Enumerable for Child Object.");
                        //c.ChildSchema.CheckObjectSchema();
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                throw new Exception($"Object Schema is invalid for {FullName}. {ex.Message}", ex);
            }
        }

        public override string ToString() => $"ObjectSchema({FullName ?? "null"})";

        public IObjectSchema Get(dynamic _) => this.Get(_);
    }

    public class ObjectSchemaException : Exception
    {
        public readonly Type? Type;
        private readonly string _message;

        public ObjectSchemaException(string message, Type? type = null)
        {
            if (type != null)
            {
                Type = type;
                _message = $"The type {type.Name} is not compatible with the object schema, {message}";
            }
            else
                _message = $"Object Schema error for Null Type, {message}";

        }
        public override string Message => _message;
    }

    public class InvalidObjectTypeException : Exception
    {
        public readonly Type? Type;
        private readonly string _message;

        public InvalidObjectTypeException(string typeName)
        {
            _message = $"The type {typeName} is not compatible with the object schema, Invalid Type";
        }
        public InvalidObjectTypeException(Type type)
        {
            Type = type;
            _message = $"The type {type.Name} is not compatible with the object schema, Invalid Type";
        }
        public override string Message => _message;
    }

    public class PropertyNotFoundException : Exception
    {
        public readonly string PropertyName;
        public readonly IObjectSchema ObjectSchema;
        private readonly string _message;
        
        public PropertyNotFoundException(string propName, IObjectSchema objectSchema)
        {
            PropertyName = propName;
            ObjectSchema = objectSchema;
            _message = $"The property {propName} was not found in {objectSchema}'s object schema";
        }
        
        public override string Message => _message;
    }

}
