using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using TFM.Nucleus.Data;
using TFM.Nucleus.Abstractions.Interfaces;

namespace TFM.Nucleus
{
    internal static class PropertySchema<T> //where T : Base
    {
        internal static List<IPropertySchema> GetAll(IObjectSchema objSchema) =>
            Extract(typeof(T).GetProperties(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public), objSchema);

        private static List<IPropertySchema> Extract(PropertyInfo[] properties, IObjectSchema objSchema)
        {
            var l = new List<IPropertySchema>();
            foreach (var p in properties)
            {
                try
                {
                    if (!p.PropertyType.IsByRef)
                    {
                        var methInfo = typeof(PropertySchema<,>).MakeGenericType(new Type[] { typeof(T), p.PropertyType }).GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic);
                        var propSchema = methInfo?.Invoke(null, new object[] { p, objSchema });
                        l.Add((IPropertySchema?)propSchema ?? throw new PropertySchemaException("Unable to invoke generic constructor for property type", p.Name, p.PropertyType));
                    }
                }
                catch (TargetInvocationException tex)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tex.InnerException ?? tex).Throw();
                    throw;
                }
            }
            return l;
        }
        //private static dynamic CreateInstance(Type t)
        //{
        //    try
        //    {
        //        if (t == typeof(string))
        //            return "";
        //        else if (t.IsInterface)
        //            return CreateInstanceOfInterface(t);
        //        else
        //            return Activator.CreateInstance(t);
        //        //else
        //        //    return Convert.ChangeType(null, t);

        //    }
        //    catch (MissingMethodException mEx)
        //    {
        //        //try private constructor
        //        try
        //        {
        //            var cons = t.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance);
        //            return cons[0].Invoke(null);
        //        }
        //        catch (Exception privateEx)
        //        {
        //            throw new AggregateException($"Unable to create instance for PropertySchema, unsupported Type[{t}]. {mEx.Message} {privateEx.Message}", new[] { privateEx, mEx });
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        throw new Exception($"Unable to create instance for PropertySchema, unsupported Type[{t}]",ex);
        //    }
        //}
        //private static dynamic CreateInstanceOfInterface(Type t)
        //{
        //    var types = AppDomain.CurrentDomain.GetAssemblies()
        //        .SelectMany(s => s.GetTypes())
        //        .Where(p => t.IsAssignableFrom(p) && !p.IsInterface);
        //    return Wrap(Activator.CreateInstance(types.First()),t);
        //}
        //private static dynamic Wrap(dynamic obj, Type type)
        //{

        //    var methInfo = typeof(GPropertySchema<T>).GetMethod(nameof(_Wrap), BindingFlags.Static | BindingFlags.NonPublic);
        //    return methInfo.MakeGenericMethod(type).Invoke(null, new object[] { obj });
        //}
        //private static TNew _Wrap<TNew>(dynamic obj) where TNew : class
        //{
        //    var d = obj as TNew;
        //    return d;
        //}
        //private static GPropertySchema<T, TPropertyType> Extract<TPropertyType>(TPropertyType _, PropertyInfo propInfo) =>
        //    new GPropertySchema<T, TPropertyType>(propInfo);
    }
    
    public class PropertySchema<T, TPropertyType> : IPropertySchema where T : CoreObject
    {
        private readonly ObjectSchema<T> _ObjectSchema;
        public readonly PropertyInfo PropertyInfo;
        public readonly FieldInfo? BackingField;
        public readonly MethodInfo? Getter;
        public readonly MethodInfo? Setter;
        //internal readonly Func<T, string, TPropertyType> _Getter;
        //internal readonly Action<string, TPropertyType> _Setter;
        //protected readonly Func<object, object> rawGetter;
        //protected readonly Action<object, object> rawSetter;
        //public readonly dynamic NewGetter;
        //public readonly dynamic NewSetter;
        public readonly dynamic? BackingFieldGetter;
        public readonly dynamic? BackingFieldSetter;
        public readonly DatabaseColumnAttribute? DBColumnAttribute;
        public readonly bool CanRead;
        public readonly bool CanWrite;

        public readonly bool RetrieveOnDemand;
        public readonly bool SuppressLogging;
        public readonly bool AbbreviatedLogging;
        public readonly bool LinkWhenLogging;
        public bool SkipOnDbRead => _skipOnDbRead;
        private readonly bool _skipOnDbRead = false;


        public readonly bool IsLinkedObject;
        public readonly bool ForceLinkedSave = false;
        public readonly bool IsLinkedChildCollection;
        public readonly Func<T?, TPropertyType?>? PropertyCreator;
        public readonly Type? ChildType;
        public readonly bool IsParentObject;
        public readonly string? ParentIDPropertyName;
        public readonly Type Type;
        //public readonly bool Mandatory;
        //public readonly eServerFunction SetValueOverride;
        //public readonly bool ReturnOutputToProperty;

        public string Name => PropertyInfo.Name;

        public bool HasFullTextIndex => DBColumnAttribute?.FullTextIndex ?? false;
        public bool IsDBColumn => DBColumnAttribute != null;
        public bool IsOverridden => IsDBColumn && DBColumnAttribute?.SetValueOverride != eServerFunction.NULL;
        public bool IsNonClusteredIndex => DBColumnAttribute?.NonClusteredIndex ?? false;
        public bool IsUnique => DBColumnAttribute?.Unique ?? false;
        //public bool ForceTimestamp => DBColumnAttribute?.ForceTimestamp ?? false;
        public IObjectSchema? ChildSchema =>
            IsLinkedChildCollection ? ObjectSchema.Get(ChildType!) : null;


        //Func<object, object> IPropertySchema.privateGetter => privateGetter;
        //Action<object, object> IPropertySchema.privateSetter => privateSetter;
        //Func<object, object> IPropertySchema.rawGetter => rawGetter;
        //Action<object, object> IPropertySchema.rawSetter => rawSetter;
        object? IPropertySchema.GetValue(ICoreObject? obj) => GetValue((T?)obj);
        IDatabaseColumnAttribute? IPropertySchema.DatabaseColumnAttribute => DBColumnAttribute;


        public TPropertyType? GetValue(T? obj) => (TPropertyType?)Getter?.Invoke(obj, null);//   obj.GetValue<TPropertyType>(Name);

        object? IPropertySchema.GetDBValue(ICoreObject? obj) => GetDBValue((T?)obj);
        object? IPropertySchema.GetDBValue(ICoreObject? obj, object? rawValue) => GetDBValue((T?)obj, rawValue);
        protected internal object? GetDBValue(T? obj) => GetDBValue(obj, obj?.GetRawCurrentValue(Name));
        protected internal object? GetDBValue(T? obj, object? rawValue)
        {
            if(obj == null)
                return DBNull.Value;

            if (obj != null && IsDBColumn)
            {
                if (IsOverridden)
                    return ServerFunction.Get(DBColumnAttribute!.SetValueOverride);
                else if (rawValue == null)
                    return DBNull.Value;
                else if (DBWriteType == rawValue.GetType())
                    return rawValue;
                else if (Type.IsAssignableTo(typeof(ICoreObject)) && rawValue is ICoreObject)
                    return ((CoreObject)rawValue).ID;
                else
                    throw new InvalidObjectTypeException(rawValue.GetType());
            }
            return DBNull.Value;
        }

        Type IPropertySchema.DBWriteType => DBWriteType;
        protected internal Type DBWriteType
        {
            get
            {
                if (Type.IsAssignableTo(typeof(ICoreObject)))
                    return typeof(Guid);
                else
                    return Type;
            }
        }

        PropertyInfo IPropertySchema.PropertyInfo => PropertyInfo;
        FieldInfo? IPropertySchema.BackingField => BackingField;
        //DBColumn? IPropertySchema.DBColumnAttribute => DBColumnAttribute;
        MethodInfo? IPropertySchema.Getter => Getter;
        MethodInfo? IPropertySchema.Setter => Setter;
        bool IPropertySchema.CanRead => CanRead;
        bool IPropertySchema.CanWrite => CanWrite;
        bool IPropertySchema.RetrieveOnDemand => RetrieveOnDemand;
        bool IPropertySchema.SuppressLogging => SuppressLogging;
        bool IPropertySchema.AbbreviatedLogging => AbbreviatedLogging;
        bool IPropertySchema.LinkWhenLogging => LinkWhenLogging;
        bool IPropertySchema.IsLinkedObject => IsLinkedObject;
        bool IPropertySchema.ForceLinkedSave => ForceLinkedSave;
        bool IPropertySchema.IsLinkedChildCollection => IsLinkedChildCollection;
        Type? IPropertySchema.ChildType => ChildType;
        bool IPropertySchema.IsParentObject => IsParentObject;
        string? IPropertySchema.ParentIDPropertyName => ParentIDPropertyName;
        Type IPropertySchema.Type => Type;
        IObjectSchema? IPropertySchema.ChildSchema => ChildSchema;
        IObjectSchema? IPropertySchema.ObjectSchema => Type is ICoreObject ? ObjectSchema.Get(Type) : null;
#pragma warning disable CS8602 // Dereference of a possibly null reference.
        object? IPropertySchema.Create(object? thisObj) => PropertyCreator((T?)thisObj);
#pragma warning restore CS8602 // Dereference of a possibly null reference.
        void IPropertySchema.SetValue(object? thisObj, object? value) => SetValue((T?)thisObj, (TPropertyType?)value);
        //public void SetValue(T thisObj, TPropertyType value) => thisObj.SetValue(value,Name);
        public void SetValue(T? obj, TPropertyType? value) => Setter?.Invoke(obj, [value]);

        internal static PropertySchema<T, TPropertyType> Create(PropertyInfo pInfo, IObjectSchema objSchema)
        {
            return new PropertySchema<T, TPropertyType>(pInfo, (ObjectSchema<T>)objSchema);
        }
        private PropertySchema(PropertyInfo pInfo, ObjectSchema<T> objSchema)
        {
            Type = pInfo.PropertyType;
            _ObjectSchema = objSchema;
            PropertyInfo = pInfo;
            RetrieveOnDemand = Attribute.IsDefined(PropertyInfo, typeof(RetrieveOnDemand));
            if (Attribute.IsDefined(PropertyInfo, typeof(DatabaseColumnAttribute)))
                DBColumnAttribute = (DatabaseColumnAttribute?)pInfo.GetCustomAttribute(typeof(DatabaseColumnAttribute));
            IsLinkedObject = Attribute.IsDefined(PropertyInfo, typeof(LinkedObject));
            if (IsLinkedObject)
                ForceLinkedSave = ((LinkedObject?)pInfo.GetCustomAttribute(typeof(LinkedObject)))?.ForceSave ?? false;
            IsLinkedChildCollection = Attribute.IsDefined(PropertyInfo, typeof(LinkedChildCollection));
            SuppressLogging = Attribute.IsDefined(PropertyInfo, typeof(SuppressLogging));
            AbbreviatedLogging = Attribute.IsDefined(PropertyInfo, typeof(AbbreviatedLogging));
            LinkWhenLogging = Attribute.IsDefined(PropertyInfo, typeof(LinkWhenLogging));
            var childInterface = Type.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(INucleusChildCollection<,>));
            if (childInterface is not null)
                ChildType = childInterface.GetGenericArguments()[0];
            else if (IsLinkedChildCollection)
            {
                ChildType = pInfo.PropertyType.GetGenericArguments()[0];
                var meth = new DynamicMethod(string.Empty, typeof(TPropertyType), new Type[] { typeof(T) });
                var il = meth.GetILGenerator();
                LocalBuilder loc_Property = il.DeclareLocal(typeof(TPropertyType));
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Newobj, typeof(TPropertyType).GetConstructor([typeof(T)]) ?? throw new NullReferenceException());
                il.Emit(OpCodes.Stloc, loc_Property);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, typeof(T).BaseType?.GetField("_properties", BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new NullReferenceException());
                il.Emit(OpCodes.Ldstr, Name);
                il.Emit(OpCodes.Ldloc, loc_Property);
                il.Emit(OpCodes.Box, typeof(TPropertyType));
                il.Emit(OpCodes.Callvirt, typeof(Dictionary<string, object>).GetMethod("set_Item") ?? throw new NullReferenceException());
                il.Emit(OpCodes.Ldloc, loc_Property);
                il.Emit(OpCodes.Ret);
                PropertyCreator = (Func<T?, TPropertyType>)meth.CreateDelegate(typeof(Func<T?, TPropertyType>));
            }
            IsParentObject = Attribute.IsDefined(PropertyInfo, typeof(ParentObject));
            if (IsParentObject)
            {
                ParentIDPropertyName = ((ParentObject?)pInfo.GetCustomAttribute(typeof(ParentObject)))?.ParentIDProperty ?? $"{pInfo.Name}ID";
                _skipOnDbRead = true;
            }

            CanRead = pInfo.CanRead;
            CanWrite = pInfo.CanWrite;
            Getter = pInfo.GetGetMethod();
            Setter = pInfo.GetSetMethod();


            //_Getter = (Func<T, string, TPropertyType>)Delegate.CreateDelegate(Expression.GetDelegateType(new Type[] { typeof(T), typeof(string), typeof(TPropertyType) }), null,
            //    typeof(T).GetMethod(nameof(CoreObject.GetValue), BindingFlags.Instance | BindingFlags.NonPublic).MakeGenericMethod(typeof(TPropertyType)));
            //_Setter = (Action<string, TPropertyType>)Delegate.CreateDelegate(Expression.GetDelegateType(new Type[] { typeof(string), typeof(TPropertyType), typeof(void) }), typeof(T),
            //    typeof(T).GetMethod(nameof(CoreObject.SetValue), BindingFlags.Instance | BindingFlags.NonPublic).MakeGenericMethod(typeof(TPropertyType)));
            //if (Setter != null)
            //    NewSetter = Delegate.CreateDelegate(Expression.GetDelegateType(new Type[] { typeof(T), Type, typeof(void) }), null, Setter);

            BackingField = TryGetBackingField();

            if (LinkWhenLogging && !Type.IsAssignableTo(typeof(ICoreObject)))
                throw new Exception($"Invalid attributes on property({Name}), property cannot be linked when logging unless it implements ICoreObject.");
            if (IsLinkedObject && IsParentObject)
                throw new Exception($"Invalid attributes on property({Name}), property cannot be both a ParentObject AND a LinkedObject.");
            if (IsLinkedObject && IsLinkedChildCollection)
                throw new Exception($"Invalid attributes on property({Name}), property cannot be both a LinkedObject AND a LinkedChildCollection.");
            if (IsLinkedChildCollection && IsParentObject)
                throw new Exception($"Invalid attributes on property({Name}), property cannot be both a ParentObject AND a LinkedChildCollection.");
        }
        //public void SetDelegateValue<TDeclaring,Tthis>(TDeclaring obj, Tthis value)
        //{
        //    var newsetter = (Action<TDeclaring, Tthis>)Delegate.CreateDelegate(typeof(Action<TDeclaring, Tthis>), null, Setter);
        //    var obj2 = ObjectSchema<TDeclaring>.Get();
        //    setter(obj,value);
        //}


        private FieldInfo? TryGetBackingField()
        {
            try
            {
                return PropertyInfo.DeclaringType?.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy).SingleOrDefault(x => x.Name == $"_{Name}" && x.FieldType.Equals(Type)) ??
                    PropertyInfo.DeclaringType?.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy).SingleOrDefault(x => x.Name.Contains($"<{Name}>") && x.FieldType == Type);

            }
            catch (Exception)
            {
                return null;
            }

        }

        public override string ToString() => $"PropertySchema({Name ?? "null"},Type({Type?.FullName ?? "null"}))";


    }

    public class PropertySchemaException : Exception
    {
        public readonly Type? Type;
        public readonly string? PropertyName;
        private readonly string _message;

        public PropertySchemaException(string message, string? propertyName = null, Type? type = null)
        {
            PropertyName = propertyName;
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
}
