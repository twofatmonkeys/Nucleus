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
        [Obsolete("Set for a [ParentObject] property and read by nothing.")]
        public bool SkipOnDbRead => _skipOnDbRead;
        private readonly bool _skipOnDbRead = false;


        public readonly bool IsLinkedObject;
        public readonly bool ForceLinkedSave = false;

        /// <summary>The [Deletable] declared on the property itself, if any. Only a child collection may carry one.</summary>
        public DeletableAttribute? DeleteDeclaration { get; }

        /// <summary>A NucleusChildCollection property marked [RetrieveOnDemand].</summary>
        public bool IsLazyChildCollection { get; }

        /// <summary>A NucleusLinkedCollection property.</summary>
        public bool IsLinkedCollection { get; }

        /// <summary>The many type of a NucleusLinkedCollection property.</summary>
        public Type? LinkedType { get; }

        /// <summary>The many type's key property, as the property's [LinkedBy] names it.</summary>
        public string? LinkedByPropertyName { get; }

        /// <summary>
        /// For a child collection, true when removing a stored child and saving the parent
        /// deletes its row: the child type is deletable through its owner. Read on first use,
        /// because the answer depends on the child type's schema and its owners'.
        /// </summary>
        public bool DeletesRemovedChildren => IsChildCollection && (ChildSchema?.IsDeletableThroughParent ?? false);

        private bool IsChildCollection => _isChildCollection;
        private readonly bool _isChildCollection;
        [Obsolete("Created the v1 child collection of a [LinkedChildCollection] property, which no longer exists. It is never set.")]
        public readonly Func<T?, TPropertyType?>? PropertyCreator;
        public readonly Type? ChildType;
        public readonly bool IsParentObject;
        public readonly string? ParentIDPropertyName;
        public readonly Type Type;
        //public readonly bool Mandatory;
        //public readonly ServerFunctionKind SetValueOverride;
        //public readonly bool ReturnOutputToProperty;

        public string Name => PropertyInfo.Name;

        public bool IsFullTextSearch => DBColumnAttribute?.FullTextSearch ?? false;
        public bool IsDBColumn => DBColumnAttribute != null;
        public bool IsOverridden => IsDBColumn && DBColumnAttribute?.SetValueOverride != ServerFunctionKind.None;
        public bool IsIndexed => DBColumnAttribute?.Indexed ?? false;
        public bool IsUnique => DBColumnAttribute?.Unique ?? false;
        //public bool ForceTimestamp => DBColumnAttribute?.ForceTimestamp ?? false;
        public IObjectSchema? ChildSchema =>
            ChildType is not null ? ObjectSchema.Get(ChildType) : null;


        //Func<object, object> IPropertySchema.privateGetter => privateGetter;
        //Action<object, object> IPropertySchema.privateSetter => privateSetter;
        //Func<object, object> IPropertySchema.rawGetter => rawGetter;
        //Action<object, object> IPropertySchema.rawSetter => rawSetter;
        object? IPropertySchema.GetValue(ICoreObject? obj) => GetValue((T?)obj);
        IDatabaseColumnAttribute? IPropertySchema.DatabaseColumnAttribute => DBColumnAttribute;


        // Compiled accessors. Property reads and writes happen on every materialisation and every
        // save, and MethodInfo.Invoke costs an argument array plus a reflection dispatch each time.
        // Null when the accessor does not exist or could not be bound, in which case the reflection
        // path is used instead.
        private readonly Func<T?, TPropertyType?>? _getInvoker;
        private readonly Action<T?, TPropertyType?>? _setInvoker;

        public TPropertyType? GetValue(T? obj)
        {
            if (_getInvoker is not null)
                return _getInvoker(obj);
            return (TPropertyType?)Getter?.Invoke(obj, null);
        }

        object? IPropertySchema.GetDBValue(ICoreObject? obj) => GetDBValue((T?)obj);
        object? IPropertySchema.GetDBValue(ICoreObject? obj, object? rawValue) => GetDBValue((T?)obj, rawValue);
        protected internal object? GetDBValue(T? obj) => GetDBValue(obj, obj?.GetRawCurrentValue(Name));
        protected internal object? GetDBValue(T? obj, object? rawValue)
        {
            if (obj == null || !IsDBColumn)
                return DBNull.Value;

            if (IsOverridden)
                return ServerFunction.Get(DBColumnAttribute!.SetValueOverride);
            if (rawValue == null)
                return DBNull.Value;

            // A linked entity is persisted as its ID.
            if (rawValue is ICoreObject linked)
                return linked.ID;

            // A Nullable<T> value boxes as its underlying type, so compare against that rather
            // than the declared property type: for an int? property DBWriteType is Nullable<Int32>
            // while the boxed value is an Int32, and an exact type comparison would reject it.
            var target = Nullable.GetUnderlyingType(DBWriteType) ?? DBWriteType;
            if (target.IsInstanceOfType(rawValue))
                return rawValue;

            throw new InvalidObjectTypeException(rawValue.GetType());
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
        DeletableAttribute? IPropertySchema.DeleteDeclaration => DeleteDeclaration;
        bool IPropertySchema.DeletesRemovedChildren => DeletesRemovedChildren;
        bool IPropertySchema.IsLazyChildCollection => IsLazyChildCollection;
        bool IPropertySchema.IsLinkedCollection => IsLinkedCollection;
        Type? IPropertySchema.LinkedType => LinkedType;
        string? IPropertySchema.LinkedByPropertyName => LinkedByPropertyName;
        Type? IPropertySchema.ChildType => ChildType;
        bool IPropertySchema.IsParentObject => IsParentObject;
        string? IPropertySchema.ParentIDPropertyName => ParentIDPropertyName;
        Type IPropertySchema.Type => Type;
        IObjectSchema? IPropertySchema.ChildSchema => ChildSchema;
        // Type is a System.Type instance, so it never *implements* ICoreObject; the question is
        // whether the type it describes does.
        IObjectSchema? IPropertySchema.ObjectSchema =>
            Type.IsAssignableTo(typeof(ICoreObject)) && !Type.IsAbstract && !Type.IsInterface
                ? ObjectSchema.Get(Type)
                : null;

        object? IPropertySchema.Create(object? thisObj) =>
            throw new PropertySchemaException(
                "Create made the v1 child collection of a [LinkedChildCollection] property, which no longer exists. " +
                "A child collection is a NucleusChildCollection property and makes itself.", Name, Type);

        /// <summary>
        /// Writes an untyped value through the property's setter. A null for a non-nullable value
        /// type is refused with an ArgumentException naming the property: the cast below would
        /// unbox null and throw NullReferenceException, and writing the type's default instead
        /// would save 0 over what the caller meant as NULL. Reads are lenient (a NULL column reads
        /// as the default); writes come from caller code, so they are strict.
        /// </summary>
        void IPropertySchema.SetValue(object? thisObj, object? value)
        {
            // Constant per closed generic: the JIT removes the test for reference and Nullable<T>
            // property types, and leaves a single null compare for a plain value type.
            if (value is null && default(TPropertyType) is not null)
                ThrowNullForValueType();
            SetValue((T?)thisObj, (TPropertyType?)value);
        }

        // Kept out of line so the cold throw does not bloat the write path.
        [System.Diagnostics.CodeAnalysis.DoesNotReturn]
        private void ThrowNullForValueType() =>
            throw new ArgumentException(
                $"{typeof(T).Name}.{Name} is {Type.Name} and cannot be set to null. Declare it {Type.Name}? to store NULL.",
                "value");
        //public void SetValue(T thisObj, TPropertyType value) => thisObj.SetValue(value,Name);
        public void SetValue(T? obj, TPropertyType? value)
        {
            if (_setInvoker is not null)
            {
                _setInvoker(obj, value);
                return;
            }
            if (Setter is null)
                throw new PropertySchemaException("The property has no setter and cannot be written.", Name, Type);
            Setter.Invoke(obj, [value]);
        }

        /// <summary>
        /// Binds a delegate to an accessor, returning null if the runtime will not bind it (an
        /// open generic or an unusual signature), so the caller falls back to reflection.
        /// </summary>
        private static TDelegate? TryBind<TDelegate>(MethodInfo? method) where TDelegate : Delegate
        {
            if (method is null)
                return null;
            try
            {
                return (TDelegate)Delegate.CreateDelegate(typeof(TDelegate), method);
            }
            catch (Exception ex) when (ex is ArgumentException or MethodAccessException or MissingMethodException)
            {
                return null;
            }
        }

        internal static PropertySchema<T, TPropertyType> Create(PropertyInfo pInfo, IObjectSchema objSchema)
        {
            return new PropertySchema<T, TPropertyType>(pInfo, (ObjectSchema<T>)objSchema);
        }
        private PropertySchema(PropertyInfo pInfo, ObjectSchema<T> objSchema)
        {
            Type = pInfo.PropertyType;
            _ObjectSchema = objSchema;
            PropertyInfo = pInfo;
            RetrieveOnDemand = Attribute.IsDefined(PropertyInfo, typeof(RetrieveOnDemandAttribute));
            if (Attribute.IsDefined(PropertyInfo, typeof(DatabaseColumnAttribute)))
                DBColumnAttribute = (DatabaseColumnAttribute?)pInfo.GetCustomAttribute(typeof(DatabaseColumnAttribute));
            IsLinkedObject = Attribute.IsDefined(PropertyInfo, typeof(LinkedObjectAttribute));
            if (IsLinkedObject)
                ForceLinkedSave = ((LinkedObjectAttribute?)pInfo.GetCustomAttribute(typeof(LinkedObjectAttribute)))?.ForceSave ?? false;
            SuppressLogging = Attribute.IsDefined(PropertyInfo, typeof(SuppressLoggingAttribute));
            AbbreviatedLogging = Attribute.IsDefined(PropertyInfo, typeof(AbbreviatedLoggingAttribute));
            LinkWhenLogging = Attribute.IsDefined(PropertyInfo, typeof(LinkWhenLoggingAttribute));
            var childInterface = Type.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(INucleusChildCollection<,>));
            _isChildCollection = childInterface is not null;
            if (childInterface is not null)
                ChildType = childInterface.GetGenericArguments()[0];
            DeleteDeclaration = (DeletableAttribute?)Attribute.GetCustomAttribute(PropertyInfo, typeof(DeletableAttribute));
            if (DeleteDeclaration is not null && !_isChildCollection)
                throw new PropertySchemaException(
                    "[Deletable] on a property applies to a NucleusChildCollection only; declare it on the entity type to make its rows deletable.", Name, Type);
            IsLazyChildCollection = RetrieveOnDemand && _isChildCollection;
            var linkedInterface = Type.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(INucleusLinkedCollection<,>));
            IsLinkedCollection = linkedInterface is not null;
            LinkedType = linkedInterface?.GetGenericArguments()[0];
            LinkedByPropertyName = ((LinkedByAttribute?)Attribute.GetCustomAttribute(PropertyInfo, typeof(LinkedByAttribute)))?.PropertyName;
            if (LinkedByPropertyName is not null && !IsLinkedCollection)
                throw new PropertySchemaException("[LinkedBy] applies to a NucleusLinkedCollection property only.", Name, Type);
            if (IsLinkedCollection)
            {
                if (LinkedByPropertyName is null)
                    throw new PropertySchemaException(
                        $"A NucleusLinkedCollection property needs [LinkedBy(nameof({LinkedType!.Name}.Key))] naming the {LinkedType.Name} property that holds the owner's ID.",
                        Name, Type);
                if (RetrieveOnDemand)
                    throw new PropertySchemaException(
                        "[RetrieveOnDemand] does not apply to a NucleusLinkedCollection, which is always loaded on first read; include it to load it with the rows.",
                        Name, Type);
                if (DBColumnAttribute is not null)
                    throw new PropertySchemaException("A NucleusLinkedCollection property is not a column; remove [DatabaseColumn].", Name, Type);
            }
            IsParentObject = Attribute.IsDefined(PropertyInfo, typeof(ParentObjectAttribute));
            if (IsParentObject)
            {
                ParentIDPropertyName = ((ParentObjectAttribute?)pInfo.GetCustomAttribute(typeof(ParentObjectAttribute)))?.ParentIDProperty ?? $"{pInfo.Name}ID";
                _skipOnDbRead = true;
            }

            CanRead = pInfo.CanRead;
            CanWrite = pInfo.CanWrite;
            // nonPublic: true as the fallback. Entity properties commonly have a protected or
            // private setter, and resolving only the public one turns every write into a no-op.
            Getter = pInfo.GetGetMethod() ?? pInfo.GetGetMethod(nonPublic: true);
            Setter = pInfo.GetSetMethod() ?? pInfo.GetSetMethod(nonPublic: true);


            //_Getter = (Func<T, string, TPropertyType>)Delegate.CreateDelegate(Expression.GetDelegateType(new Type[] { typeof(T), typeof(string), typeof(TPropertyType) }), null,
            //    typeof(T).GetMethod(nameof(CoreObject.GetValue), BindingFlags.Instance | BindingFlags.NonPublic).MakeGenericMethod(typeof(TPropertyType)));
            //_Setter = (Action<string, TPropertyType>)Delegate.CreateDelegate(Expression.GetDelegateType(new Type[] { typeof(string), typeof(TPropertyType), typeof(void) }), typeof(T),
            //    typeof(T).GetMethod(nameof(CoreObject.SetValue), BindingFlags.Instance | BindingFlags.NonPublic).MakeGenericMethod(typeof(TPropertyType)));
            //if (Setter != null)
            //    NewSetter = Delegate.CreateDelegate(Expression.GetDelegateType(new Type[] { typeof(T), Type, typeof(void) }), null, Setter);

            _getInvoker = TryBind<Func<T?, TPropertyType?>>(Getter);
            _setInvoker = TryBind<Action<T?, TPropertyType?>>(Setter);

            BackingField = TryGetBackingField();

            if (LinkWhenLogging && !Type.IsAssignableTo(typeof(ICoreObject)))
                throw new PropertySchemaException("[LinkWhenLogging] requires a property whose type implements ICoreObject.", Name, Type);
            if (IsLinkedObject && IsParentObject)
                throw new PropertySchemaException("A property cannot be both [ParentObject] and [LinkedObject].", Name, Type);
        }
        //public void SetDelegateValue<TDeclaring,Tthis>(TDeclaring obj, Tthis value)
        //{
        //    var newsetter = (Action<TDeclaring, Tthis>)Delegate.CreateDelegate(typeof(Action<TDeclaring, Tthis>), null, Setter);
        //    var obj2 = ObjectSchema<TDeclaring>.Get();
        //    setter(obj,value);
        //}


        /// <summary>
        /// Finds the field behind the property, either a hand written <c>_name</c> field or the
        /// compiler generated <c>&lt;Name&gt;k__BackingField</c>. FirstOrDefault, not
        /// SingleOrDefault: a shadowed member legitimately yields more than one match and the most
        /// derived one wins, where SingleOrDefault threw and the caller swallowed it as "none".
        /// </summary>
        private FieldInfo? TryGetBackingField()
        {
            var fields = PropertyInfo.DeclaringType?.GetFields(BindingFlags.NonPublic | BindingFlags.Instance
                | BindingFlags.Public | BindingFlags.FlattenHierarchy);
            if (fields is null)
                return null;

            return fields.FirstOrDefault(x => x.Name == $"_{Name}" && x.FieldType == Type)
                ?? fields.FirstOrDefault(x => x.Name.Contains($"<{Name}>") && x.FieldType == Type);
        }

        public override string ToString() => $"PropertySchema({Name ?? "null"},Type({Type?.FullName ?? "null"}))";


    }

    public class PropertySchemaException : TFM.Nucleus.NucleusException
    {
        public Type? Type { get; }
        public string? PropertyName { get; }

        public PropertySchemaException(string message, string? propertyName = null, Type? type = null,
            Exception? innerException = null)
            : base(BuildMessage(message, propertyName, type), innerException)
        {
            PropertyName = propertyName;
            Type = type;
        }

        private static string BuildMessage(string message, string? propertyName, Type? type)
        {
            var subject = (propertyName, type) switch
            {
                (not null, not null) => $"Property '{propertyName}' of type {type.Name}",
                (not null, null) => $"Property '{propertyName}'",
                (null, not null) => $"Type {type.Name}",
                _ => "Property schema",
            };
            return $"{subject}: {message}";
        }
    }
}
