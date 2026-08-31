using TFM.Nucleus.Data;
using static System.Runtime.InteropServices.JavaScript.JSType;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel;
using System.Data.Common;
using System.Data;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text;
using Microsoft.Extensions.Logging;
using TFM.Nucleus.Abstractions.Interfaces;

namespace TFM.Nucleus
{
    [TypeConverter(typeof(NucleusTypeConverter))]
    public abstract class CoreObject :
        ICoreObject, INotifyPropertyChanged, IGuidIdentifier
    {

        private readonly Dictionary<string, object?> _properties = [];
        private readonly Dictionary<string, object?> _newProperties = [];
        private bool _isValidated = false;


        public event PropertyChangedEventHandler? PropertyChanged;

        public bool IsNew => _isNew;
        internal bool _isNew = true;
        public bool IsReadOnly => _isReadOnly;
        private bool _isReadOnly = false;

        [JsonIgnore]
        public IDataProvider? DataProvider { get; set; }

        [DatabaseColumnAttribute(PrimaryKey = true)]
        public Guid ID
        {
            get { return GetValue<Guid>(); }
        }
        [DatabaseColumnAttribute(NonClusteredIndex = true, IndexSortDESC = true,
            SetValueOverride = eServerFunction.SYSDATETIMEOFFSET, ReturnOutputToProperty = true)]
        public DateTimeOffset Timestamp
        {
            get { return GetValue<DateTimeOffset>(); }
        }


        IObjectSchema ICoreObject.ObjectSchema => ObjectSchema;
        protected internal IObjectSchema ObjectSchema => Nucleus.ObjectSchema.Get(GetType());

        Dictionary<string, object?> ICoreObject.ChangedProperties => ChangedProperties;
        protected internal Dictionary<string, object?> ChangedProperties => IsNew ? _properties : _newProperties;

        protected internal bool HasChangedProperties => IsReadOnly ? false : ChangedProperties.Count > 0;



        private void CommitChanges(bool makeReadOnly, ILogger? logger = null) => CommitChanges(makeReadOnly, true, logger);
        private void CommitChanges(bool makeReadOnly, bool isRootObj, ILogger? logger = null)
        {
            LogChanges(logger);
            if (makeReadOnly)
                _isReadOnly = true;
            if (!IsNew)
            {
                foreach (var newProp in _newProperties)
                    _properties[newProp.Key] = newProp.Value;
                _newProperties.Clear();
            }
            _properties["Timestamp"] = DateTimeOffset.Now;
            _isNew = false;
            if(ObjectSchema.HasNucleusChildCollections)
                foreach(var coll in NucleusChildCollections)
                    foreach(var child in coll.ChildObjects)
                        child.CommitChanges();
            if (isRootObj)
                if (HasChangedLinkedObjects(out var objs))
                    foreach (CoreObject obj in objs!)
                        obj.CommitChanges(makeReadOnly, false);
        }

        void ICoreObject.ChildPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e) => ChildPropertyChanged(sender, e);
        protected internal void SetValue<T>(T? value, [CallerMemberName] string? property = null)
        {
            ArgumentNullException.ThrowIfNull(property);
            if (IsReadOnly)
                throw new ReadOnlyException($"Unable to set the {property} property. {ObjectSchema.Name}({this}) is currently read only.");
            else
            {
                if (IsNew)
                {
                    _properties[property] = value;
                }
                else
                {
                    if (_newProperties.TryGetValue(property, out var existing) && Equals(existing, value))
                        return;
                    _newProperties[property] = value;
                }
                _isValidated = false;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
            }
        }
        
        public IEnumerable<INucleusChildCollection> NucleusChildCollections
        {
            get
            {
                if (ObjectSchema.HasNucleusChildCollections)
                    foreach (var p in ObjectSchema.NucleusChildCollectionProperties!)
                        if (_properties.TryGetValue(p.Name, out var value) && value != null && value is INucleusChildCollection collection)
                            yield return collection;
            }
        }

        protected internal NucleusChildCollection<TChild, TParent> GetChildCollection<TChild, TParent>([CallerMemberName] string? property = null) 
            where TParent : CoreObject<TParent>, new()
            where TChild : ICoreChildObject<TChild, TParent>, new()
        {
            ArgumentNullException.ThrowIfNull(property);
            if (!_properties.TryGetValue(property, out var value) || value is not NucleusChildCollection<TChild, TParent> collection)
            {
                collection = new NucleusChildCollection<TChild, TParent>((TParent)this);
                _properties[property] = collection;
            }
            return collection;
        }

        

        protected internal T? GetValue<T>([CallerMemberName] string? property = null)
        {
            ArgumentNullException.ThrowIfNull(property);
            if (!IsReadOnly && !IsNew)
                if (_newProperties.TryGetValue(property, out var newValue))
                    return (T?)newValue;
            if (_properties.TryGetValue(property, out var value))
                return (T?)value;
            else
                return default;
        }

        internal object? GetRawCurrentValue(string property)
        {
            if (!(IsReadOnly || IsNew) && _newProperties.TryGetValue(property, out var newValue))
                return newValue;
            else if (_properties.TryGetValue(property, out var oldValue))
                return oldValue;
            return null;
        }
        internal void SetRawCurrentValue(string property, object? obj)
        {
            _isValidated = false;
            if (IsReadOnly || IsNew)
                _properties[property] = obj;
            else
                _newProperties[property] = obj;
        }
        internal void BindValue(string property, object? obj)
        {
            //if ID exists in the properties collection, then this is not a new object and changes should be tracked in the _newProperties collection,
            //otherwise changes should be made to the _properties collection and the object should be considered new until an ID value is bound to it.
            //This allows for proper tracking of changes to the object from the moment of instantiation, which is necessary for accurate logging of changes and linked objects when the object is saved to the database.
            if (property == "ID")
                _isNew = false;
            _properties[property] = obj != DBNull.Value ? obj : null;

        }

        private void LogChanges(ILogger? logger = null)
        {
            try
            {
                if (!ObjectSchema.SuppressLogging || logger != null)
                {
                    var sb = new StringBuilder(IsNew ? $"Created {this}" : $"Updated {this}");
                    if (!IsNew)
                        sb.Append(_getChangeLogEntry());
                    logger?.LogInformation(sb.ToString());
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, $"Error logging changes for {this}");
            }
        }
        private string _getChangeLogEntry()
        {
            if (!IsReadOnly && !IsNew && HasChangedProperties)
            {
                var sb = new StringBuilder();
                foreach (var prop in ChangedProperties)
                {
                    if (!ObjectSchema.GetProperty(prop.Key).SuppressLogging)
                    {
                        sb.Append(", ");
                        if (ObjectSchema.GetProperty(prop.Key).AbbreviatedLogging)
                        {
                            if (prop.Value == null)
                                sb.Append($"cleared value from {prop.Key}");
                            else
                                sb.Append($"updated {prop.Key} to a new value");
                        }
                        else
                        {
                            if (prop.Value == null)
                            {
                                if (_properties.TryGetValue(prop.Key, out var value))
                                {
                                    if (value != null)
                                        sb.Append($"removed {value} from {prop.Key}");
                                    else
                                        sb.Append($"cleared {prop.Key}");
                                }
                                else
                                    sb.Append($"cleared {prop.Key}");
                            }
                            else
                            {
                                if (_properties.TryGetValue(prop.Key, out var value))
                                {
                                    if (value == null)
                                        sb.Append($"set {prop.Key}");
                                    else
                                        sb.Append($"updated {prop.Key} from {value}");
                                }
                                else
                                    sb.Append($"set {prop.Key}");
                            }
                        }
                    }
                }
                return sb.Append('.').ToString();
            }
            else
                return "";
        }


        private static void Init<T>(out T assignTo, T value)
        {
            assignTo = value;
        }
        #region ICoreObject 

        T? ICoreObject.GetPropertyValue<T>(string propertyName) where T : default => GetValue<T>(propertyName);

        object? ICoreObject.GetPropertyValue(string propertyName) => GetRawCurrentValue(propertyName);

        void ICoreObject.BindValue(string property, object? value) => BindValue(property, value);

        void ICoreObject.CommitChanges(ILogger? logger) => CommitChanges(false, logger);

        #endregion



        bool ICoreObject.HasChangedLinkedObjects(out ICoreObject[]? objects)
        {
            if(HasChangedLinkedObjects(out var objs))
            {
                objects = objs!.Cast<ICoreObject>().ToArray();
                return true;
            }
            objects = null;
            return false;
        }

        protected internal bool HasChangedLinkedObjects(out CoreObject[]? objects)
        {
            if (!(ObjectSchema.HasLinkedObjects || ObjectSchema.HasLinkedChildObjects))
            {
                objects = null;
                return false;
            }
            else
            {
                return _hasChangedLinkedObjects(out objects);
            }
        }
        private bool _hasChangedLinkedObjects(out CoreObject[]? objects)
        {
            var objList = new List<CoreObject>();
            var exclusions = new Dictionary<Guid, object?>() { { ID, null } };
            if (ObjectSchema.HasLinkedChildObjects)
                foreach (var child in ObjectSchema.GetLinkedChildObjects(this).Cast<CoreObject>())
                    child._checkLinkedObjects(objList, exclusions);

            if (ObjectSchema.HasLinkedObjects)
                foreach (var prop in ObjectSchema.LinkedObjectProperties!)
                    if (prop.ForceLinkedSave)
                        if (GetRawCurrentValue(prop.Name) is CoreObject @object)
                            @object._checkLinkedObjects(objList, exclusions);

            objects = objList.ToArray();
            return objList.Count > 0;
        }



        private void _checkLinkedObjects(List<CoreObject> outCollection, Dictionary<Guid, object?> exclusions)
        {
            if (!exclusions.ContainsKey(ID))
            {
                if (HasChangedProperties)
                    outCollection.Add(this);
                exclusions.Add(ID, null);
                if (ObjectSchema.HasLinkedObjects)
                    foreach (var prop in ObjectSchema.LinkedObjectProperties!)
                        if (prop.ForceLinkedSave)
                            if (GetRawCurrentValue(prop.Name) is CoreObject @object)
                                @object._checkLinkedObjects(outCollection, exclusions);


                if (ObjectSchema.HasLinkedChildObjects)
                    foreach (CoreObject child in ObjectSchema.GetLinkedChildObjects(this))
                        child._checkLinkedObjects(outCollection, exclusions);
            }
        }


        void ICoreObject.OnOutputParameterReceived(object? sender, IOutputReceivedEventArgs args) =>
        OnOutputParameterReceived(sender, args);
        protected internal void OnOutputParameterReceived(object? sender, IOutputReceivedEventArgs args)
        {
            _properties[args.PropertyName!] = args.Value!;
            if (sender is not null and IQueryOutputParameter)
            {
                ((IQueryOutputParameter)sender).OutputReceived -= OnOutputParameterReceived;
            }
        }





        [JsonIgnore]
        [SuppressCopying]
        private IDataContext? data_Context
        {
            get
            {
                if (_dataContext != null)
                {
                    try
                    {
                        _dataContext.ToString();
                        return _dataContext;
                    }
                    catch (ObjectDisposedException)
                    {
                        _dataContext = null;
                        return null;
                    }
                    catch (Exception ex)
                    {
                        throw new Exception($"Error obtaining the object's data context. {ex.Message}", ex);
                    }
                }
                return null;
            }
            set { _dataContext = value; }
        }
        [JsonIgnore]
        [SuppressCopying]
        private IDataContext? _dataContext;











        public string DisplayName { get { return this.ToString(); } }




        //Overwrite these properties in child classes to change behaviour
        protected internal virtual bool DisableLogging => false; //Setting true disables logging of actions within the Save() method of the object instance.
        protected internal virtual bool AllowCaching => true; //Setting true allows the object collection to be stored in the cache, false forces retrieval from the DB in every case
        protected internal virtual bool AllowDeletions => false;
        protected internal virtual int CacheRefreshInterval => 120; //Cache refresh interval in seconds
        protected internal virtual int CacheLifetime => 900; //Cache lifetime interval in seconds

        public static bool IsDerivedClass(Type t)
        {
            return t.IsSubclassOf(typeof(ICoreObject));
        }
        public static bool IsDerivedClass<T>()
        {
            return IsDerivedClass(typeof(T));
        }



        public override bool Equals(object? obj)
        {
            if (obj == null)
                return false;
            if (obj.GetType() == this.GetType())
            {
                if (this.ID == ((CoreObject)obj).ID)
                    return true;
            }
            return false;
        }
        public string ToJson()
        {
            return System.Text.Json.JsonSerializer.Serialize(this);

        }
        public override int GetHashCode()
        {
            return this.ID.GetHashCode();
        }
        public static bool operator ==(CoreObject? x, CoreObject? y)
        {

            if (x is null && y is null)
                return true;
            else if (x is null || y is null)
                return false;

            return (x.ID == y.ID);

        }
        public static bool operator !=(CoreObject? x, CoreObject? y)
        {
            return !(x == y);
        }

        public override string ToString()
        {
            return $"{ObjectSchema.Name} - {ID}";
        }
        public static explicit operator Guid(CoreObject b)
        {
            return b.ID;
        }


        public CoreObject()
        {
            _properties[nameof(ID)] = Guid.NewGuid();
            Changes = [];
        }

















        [SuppressCopying]
        private static ConcurrentDictionary<Type, Guid> _CurrentCache = new ConcurrentDictionary<Type, Guid>();











        protected internal virtual void OnAfterLoadFromDB()
        {

        }
        protected internal virtual void OnAfterInitializeDB()
        {

        }

        public virtual void Validate()
        {
            _isValidated = true;
            if (_ValidationRootFlag)
            {
                if (ObjectSchema.HasParentObject)
                    throw new CoreObjectException(CoreObjectExceptionType.UnableToSaveFromChildObject);
                if (HasChangedLinkedObjects(out var objs))
                    foreach (CoreObject obj in objs!.Where(x => !x._isValidated).ToList())
                        obj.LinkedObjectValidate();
            }

        }
        private bool _ValidationRootFlag = true;
        private void LinkedObjectValidate()
        {
            _ValidationRootFlag = false;
            Validate();
            _ValidationRootFlag = true;
        }
        public virtual void PreSave()
        {
            //perform validation logic here override in child classes

        }
        public virtual void Save()
        {
            Validate();
            EnsureSaveable();
            NucleusRuntime.Current.Save(this);
        }

        public virtual Task SaveAsync() => SaveAsync(CancellationToken.None);
        public virtual Task SaveAsync(CancellationToken token)
        {
            Validate();
            EnsureSaveable();
            return NucleusRuntime.Current.SaveAsync(this, token);
        }

        /// <summary>Saves within a caller-owned context (and transaction); the caller commits or rolls back.</summary>
        public virtual async Task SaveAsync(IDataContext dataContext, CancellationToken token = default)
        {
            ArgumentNullException.ThrowIfNull(dataContext);
            Validate();
            EnsureSaveable();
            await dataContext.SaveAsync(this);
            CommitChanges(false);
        }

        private void EnsureSaveable()
        {
            if (!_isValidated)
                throw new InvalidOperationException($"{this} is in an invalid state and cannot be saved. _Validate() method must be called from all overidden Validate() methods.");
            if (IsReadOnly)
                throw new ReadOnlyException($"{this} is locked as read only.");
        }







        public static void BulkSave<TObject>(IEnumerable<TObject> objs) where TObject : CoreObject, new() =>
            NucleusRuntime.Current.BulkSave(objs);

        /// <summary>Deletes this object (and its loaded child collections); the type must be [Deletable].</summary>
        public void Delete()
        {
            NucleusRuntime.Current.Delete(this);
        }

        public async Task DeleteAsync(CancellationToken token = default)
        {
            await NucleusRuntime.Current.DeleteAsync(this, token);
        }




















        
        
        
        #region Static load API (forwarders over the ambient runtime)

        public static T? LoadByID<T>(Guid id) where T : CoreObject<T>, new() =>
            NucleusRuntime.Current.LoadByID<T>(id);

        public static bool TryLoadByID<T>(Guid id, out T? obj) where T : CoreObject<T>, new()
        {
            obj = LoadByID<T>(id);
            return obj != null;
        }

        public static T? LoadObjectWhere<T>(Expression<Func<T, bool>> expression) where T : CoreObject<T>, new() =>
            LoadWhere(expression).FirstOrDefault();

        public static NucleusCollection<T> LoadAll<T>() where T : CoreObject<T>, new() =>
            NucleusRuntime.Current.LoadAll<T>();

        public static NucleusCollection<T> LoadWhere<T>(Expression<Func<T, bool>> expression) where T : CoreObject<T>, new() =>
            NucleusRuntime.Current.LoadWhere<T>(expression.Body);

        public static Task<NucleusCollection<T>> LoadWhereAsync<T>(Expression<Func<T, bool>> expression, CancellationToken token = default) where T : CoreObject<T>, new() =>
            NucleusRuntime.Current.LoadWhereAsync<T>(expression.Body, token);

        #endregion

        private static Dictionary<string, object?>[] CreateDictionaryFromDT<T>(DataTable dt)
        {
            return CreateDictionaryFromDT(dt, typeof(T));
        }
        private static Dictionary<string, object?>[] CreateDictionaryFromDT(DataTable dt, Type type)
        {
            //todo - can be optimised further using multiple threads
            var rowCount = dt.Rows.Count;
            var values = new Dictionary<string, object?>[rowCount];
            try
            {
                var properties = Nucleus.ObjectSchema.Get(type).AllDBColumns.Where(x => x.CanWrite).ToArray();
                int i = 0;
                foreach (DataRow dr in dt.Rows)
                {
                    var dict = new Dictionary<string, object?>();
                    foreach (var prop in properties)
                    {
                        object? o = dr[prop.Name];
                        dict.Add(prop.Name, o.Equals(DBNull.Value) ? null : o);
                    }
                    values[i] = dict;
                    i++;
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Unable to convert the " + type.Name + " data structure into a Dictionary collection.", ex);
            }
            return values;

        }








        private static List<T> BuildObjectCollection<T>(Dictionary<string, object>[] sqlData) where T : CoreObject, new()
        {
            try
            {
                Type[] methodArgs = { typeof(Dictionary<string, object>[]) };

                DynamicMethod Extract = new DynamicMethod("Extract", typeof(List<T>), methodArgs, true);
                ILGenerator il = Extract.GetILGenerator();

                LocalBuilder loc_objlist = il.DeclareLocal(typeof(List<T>));
                LocalBuilder loc_newobject = il.DeclareLocal(typeof(T));
                LocalBuilder loc_index = il.DeclareLocal(typeof(int));
                LocalBuilder loc_dictionary = il.DeclareLocal(typeof(Dictionary<string, object>));
                LocalBuilder loc_placeholder = il.DeclareLocal(typeof(object));
                MethodInfo mth_OnAfterLoad = typeof(T).GetMethod("OnAfterLoadFromDB", BindingFlags.NonPublic | BindingFlags.Instance)!;

                Label enterloop = il.DefineLabel();
                Label loopagain = il.DefineLabel();

                //create a new list to store and return
                il.Emit(OpCodes.Newobj, typeof(List<T>).GetConstructors()[0]);
                il.Emit(OpCodes.Stloc, loc_objlist);

                //create index value 0 and store
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Stloc, loc_index);

                //begin loop
                il.Emit(OpCodes.Br, enterloop);
                il.MarkLabel(loopagain);

                //create new TFM object
                il.Emit(OpCodes.Newobj, typeof(T).GetConstructors()[0]);
                il.Emit(OpCodes.Stloc, loc_newobject);

                //set loading flag
                il.Emit(OpCodes.Ldloc, loc_newobject);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Callvirt, typeof(T).GetProperty("IsLoading")!.GetSetMethod()!);


                //set the properties
                //load the currently indexed dictionary item from the list and store local
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldloc, loc_index);
                il.Emit(OpCodes.Ldelem_Ref);
                il.Emit(OpCodes.Stloc, loc_dictionary);
                var setters = new Dictionary<string, MethodInfo>();
                foreach (KeyValuePair<string, object> k in sqlData[0])
                {
                    var setter = ObjectSchema<T>.Get().dProperties.GetValueOrDefault(k.Key)!.GetSetMethod() ?? 
                        ObjectSchema<T>.Get().dProperties.GetValueOrDefault(k.Key)!.GetSetMethod(true) ??
                        throw new NullReferenceException();
                    setters.Add(k.Key, setter!);
                }
                //// loop through all the properties to add to the object load the named object from the Dictionary to the stack
                for (int index = 0; index < sqlData[0].Count; ++index)
                {
                    KeyValuePair<string, object> kvp = sqlData[0].ElementAt(index);
                    var propType = ObjectSchema<T>.Get().dProperties.GetValueOrDefault(kvp.Key)!.PropertyType;
                    Label skipsetproperty = il.DefineLabel();
                    il.Emit(OpCodes.Ldloc, loc_dictionary);
                    il.Emit(OpCodes.Ldstr, kvp.Key);
                    il.Emit(OpCodes.Callvirt, typeof(Dictionary<string, object>).GetMethod("get_Item")!);
                    if (typeof(TFMInitializeWithString).IsAssignableFrom(propType))
                    {
                        //create new object
                        var typeConst = propType.GetConstructor(new Type[3] { typeof(string), typeof(bool), typeof(bool) });
                        il.Emit(OpCodes.Ldc_I4_1);
                        il.Emit(OpCodes.Ldc_I4_0);
                        il.Emit(OpCodes.Newobj, typeConst!);
                    }



                    il.Emit(OpCodes.Dup);
                    il.Emit(OpCodes.Stloc, loc_placeholder);
                    il.Emit(OpCodes.Brfalse_S, skipsetproperty);
                    //set the property if its not null or empty
                    il.Emit(OpCodes.Ldloc, loc_newobject);
                    il.Emit(OpCodes.Ldloc, loc_placeholder);
                    il.Emit(OpCodes.Unbox_Any, propType);
                    il.Emit(OpCodes.Callvirt, setters.ElementAt(index).Value);
                    il.MarkLabel(skipsetproperty);
                }
                //set loading flag
                il.Emit(OpCodes.Ldloc, loc_newobject);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Callvirt, typeof(T).GetProperty("IsLoading").GetSetMethod());

                //call OnAfterLoadFromDB()
                il.Emit(OpCodes.Ldloc, loc_newobject);
                il.Emit(OpCodes.Call, mth_OnAfterLoad);

                //load list onto the stack, clear changes and add the created object
                il.Emit(OpCodes.Ldloc, loc_objlist);
                il.Emit(OpCodes.Ldloc, loc_newobject);
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldfld, typeof(T).BaseType.GetField("Changes"));
                il.Emit(OpCodes.Callvirt, typeof(ChangeCollection).GetMethod("Clear"));
                il.Emit(OpCodes.Callvirt, typeof(List<T>).GetMethod("Add"));

                //add 1 to the index
                il.Emit(OpCodes.Ldloc, loc_index);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Stloc, loc_index);

                //entry point for first loop
                il.MarkLabel(enterloop);
                il.Emit(OpCodes.Ldloc, loc_index);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldlen);
                // if there are still items in the list loop again    
                il.Emit(OpCodes.Blt, loopagain);

                //load list onto stack and return
                il.Emit(OpCodes.Ldloc, loc_objlist);
                il.Emit(OpCodes.Ret);

                //Invoke and return
                object[] prms = { sqlData };
                return (List<T>)Extract.Invoke(null, prms);

            }
            catch (Exception ex)
            {
                throw new Exception("Unable to convert the " + typeof(T).Name + " Dictionary structure into an object collection.", ex);
            }

        }
        private static List<object> BuildObjectCollection(Dictionary<string, object>[] sqlData, Type type)
        {
            try
            {
                Type[] methodArgs = { typeof(Dictionary<string, object>[]) };
                var objectSchema = Nucleus.ObjectSchema.Get(type);

                DynamicMethod Extract = new DynamicMethod("Extract", typeof(List<object>), methodArgs, true);
                ILGenerator il = Extract.GetILGenerator();

                LocalBuilder loc_objlist = il.DeclareLocal(typeof(List<object>));
                LocalBuilder loc_newobject = il.DeclareLocal(type);
                LocalBuilder loc_index = il.DeclareLocal(typeof(int));
                LocalBuilder loc_dictionary = il.DeclareLocal(typeof(Dictionary<string, object>));
                LocalBuilder loc_placeholder = il.DeclareLocal(typeof(object));
                MethodInfo mth_OnAfterLoad = type.GetMethod("OnAfterLoadFromDB", BindingFlags.NonPublic | BindingFlags.Instance);

                Label enterloop = il.DefineLabel();
                Label loopagain = il.DefineLabel();

                //create a new list to store and return
                il.Emit(OpCodes.Newobj, typeof(List<object>).GetConstructors()[0]);
                il.Emit(OpCodes.Stloc, loc_objlist);

                //create index value 0 and store
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Stloc, loc_index);

                //begin loop
                il.Emit(OpCodes.Br, enterloop);
                il.MarkLabel(loopagain);

                //create new TFM object
                il.Emit(OpCodes.Newobj, type.GetConstructors()[0]);
                il.Emit(OpCodes.Stloc, loc_newobject);

                //set loading flag
                il.Emit(OpCodes.Ldloc, loc_newobject);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Callvirt, type.GetProperty("IsLoading").GetSetMethod());


                //set the properties
                //load the currently indexed dictionary item from the list and store local
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldloc, loc_index);
                il.Emit(OpCodes.Ldelem_Ref);
                il.Emit(OpCodes.Stloc, loc_dictionary);
                var setters = new Dictionary<string, MethodInfo>();
                foreach (KeyValuePair<string, object> k in sqlData[0])
                {
                    setters.Add(k.Key, objectSchema.GetProperty(k.Key).Setter ?? throw new Exception("Setter is null"));
                }
                //// loop through all the properties to add to the object load the named object from the Dictionary to the stack
                for (int index = 0; index < sqlData[0].Count; ++index)
                {
                    KeyValuePair<string, object> kvp = sqlData[0].ElementAt(index);
                    var propType = objectSchema.GetProperty(kvp.Key).Type;
                    Label skipsetproperty = il.DefineLabel();
                    il.Emit(OpCodes.Ldloc, loc_dictionary);
                    il.Emit(OpCodes.Ldstr, kvp.Key);
                    il.Emit(OpCodes.Callvirt, typeof(Dictionary<string, object>).GetMethod("get_Item"));
                    if (typeof(TFMInitializeWithString).IsAssignableFrom(propType))
                    {
                        //create new object
                        var typeConst = propType.GetConstructor(new Type[3] { typeof(string), typeof(bool), typeof(bool) });
                        il.Emit(OpCodes.Ldc_I4_1);
                        il.Emit(OpCodes.Ldc_I4_0);
                        il.Emit(OpCodes.Newobj, typeConst);
                    }



                    il.Emit(OpCodes.Dup);
                    il.Emit(OpCodes.Stloc, loc_placeholder);
                    il.Emit(OpCodes.Brfalse_S, skipsetproperty);
                    //set the property if its not null or empty
                    il.Emit(OpCodes.Ldloc, loc_newobject);
                    il.Emit(OpCodes.Ldloc, loc_placeholder);
                    il.Emit(OpCodes.Unbox_Any, propType);
                    il.Emit(OpCodes.Callvirt, setters.ElementAt(index).Value);
                    il.MarkLabel(skipsetproperty);
                }
                //set loading flag
                il.Emit(OpCodes.Ldloc, loc_newobject);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Callvirt, type.GetProperty("IsLoading").GetSetMethod());

                //call OnAfterLoadFromDB()
                il.Emit(OpCodes.Ldloc, loc_newobject);
                il.Emit(OpCodes.Call, mth_OnAfterLoad);

                //load list onto the stack, clear changes and add the created object
                il.Emit(OpCodes.Ldloc, loc_objlist);
                il.Emit(OpCodes.Ldloc, loc_newobject);
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldfld, type.BaseType.GetField("Changes"));
                il.Emit(OpCodes.Callvirt, typeof(ChangeCollection).GetMethod("Clear"));
                il.Emit(OpCodes.Callvirt, typeof(List<object>).GetMethod("Add"));

                //add 1 to the index
                il.Emit(OpCodes.Ldloc, loc_index);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Stloc, loc_index);

                //entry point for first loop
                il.MarkLabel(enterloop);
                il.Emit(OpCodes.Ldloc, loc_index);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldlen);
                // if there are still items in the list loop again    
                il.Emit(OpCodes.Blt, loopagain);

                //load list onto stack and return
                il.Emit(OpCodes.Ldloc, loc_objlist);
                il.Emit(OpCodes.Ret);

                //Invoke and return
                object[] prms = { sqlData };
                return (List<object>)Extract.Invoke(null, prms);

            }
            catch (Exception ex)
            {
                throw new Exception("Unable to convert the " + type.Name + " Dictionary structure into an object collection.", ex);
            }

        }

        private static List<object> ExtractCollection(Dictionary<string, object>[] val, Type type)
        {
            try
            {
                Type[] methodArgs = { typeof(Dictionary<string, object>[]) };
                var objectSchema = Nucleus.ObjectSchema.Get(type);

                DynamicMethod Extract = new DynamicMethod("Extract", typeof(List<object>), methodArgs, true);
                ILGenerator il = Extract.GetILGenerator();

                LocalBuilder loc_objlist = il.DeclareLocal(typeof(List<>).MakeGenericType(type));
                LocalBuilder loc_newobject = il.DeclareLocal(type);
                LocalBuilder loc_index = il.DeclareLocal(typeof(int));
                LocalBuilder loc_dictionary = il.DeclareLocal(typeof(Dictionary<string, object>));
                LocalBuilder loc_placeholder = il.DeclareLocal(typeof(object));
                MethodInfo mth_OnAfterLoad = type.GetMethod("OnAfterLoadFromDB", BindingFlags.NonPublic | BindingFlags.Instance);

                Label enterloop = il.DefineLabel();
                Label loopagain = il.DefineLabel();

                //create a new list to store and return
                il.Emit(OpCodes.Newobj, typeof(List<object>).GetConstructors()[0]);
                il.Emit(OpCodes.Stloc, loc_objlist);

                //create index value 0 and store
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Stloc, loc_index);

                //begin loop
                il.Emit(OpCodes.Br, enterloop);
                il.MarkLabel(loopagain);

                //create new TFM object
                il.Emit(OpCodes.Newobj, type.GetConstructors()[0]);
                il.Emit(OpCodes.Stloc, loc_newobject);

                //set loading flag
                il.Emit(OpCodes.Ldloc, loc_newobject);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Callvirt, type.GetProperty("IsLoading").GetSetMethod());


                //set the properties
                //load the currently indexed dictionary item from the list and store local
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldloc, loc_index);
                il.Emit(OpCodes.Ldelem_Ref);
                il.Emit(OpCodes.Stloc, loc_dictionary);
                var setters = new Dictionary<string, MethodInfo>();
                foreach (KeyValuePair<string, object> k in val[0])
                {
                    setters.Add(k.Key, objectSchema.GetProperty(k.Key).Setter ?? throw new Exception("Setter is null"));
                }
                //// loop through all the properties to add to the object load the named object from the Dictionary to the stack
                for (int index = 0; index < val[0].Count; ++index)
                {
                    KeyValuePair<string, object> kvp = val[0].ElementAt(index);
                    var propType = objectSchema.GetProperty(kvp.Key).Type;
                    Label skipsetproperty = il.DefineLabel();
                    il.Emit(OpCodes.Ldloc, loc_dictionary);
                    il.Emit(OpCodes.Ldstr, kvp.Key);
                    il.Emit(OpCodes.Callvirt, typeof(Dictionary<string, object>).GetMethod("get_Item"));
                    if (typeof(TFMInitializeWithString).IsAssignableFrom(propType))
                    {
                        //create new object
                        var typeConst = propType.GetConstructor(new Type[3] { typeof(string), typeof(bool), typeof(bool) });
                        il.Emit(OpCodes.Ldc_I4_1);
                        il.Emit(OpCodes.Ldc_I4_0);
                        il.Emit(OpCodes.Newobj, typeConst);
                    }



                    il.Emit(OpCodes.Dup);
                    il.Emit(OpCodes.Stloc, loc_placeholder);
                    il.Emit(OpCodes.Brfalse_S, skipsetproperty);
                    //set the property if its not null or empty
                    il.Emit(OpCodes.Ldloc, loc_newobject);
                    il.Emit(OpCodes.Ldloc, loc_placeholder);
                    il.Emit(OpCodes.Unbox_Any, propType);
                    il.Emit(OpCodes.Callvirt, setters.ElementAt(index).Value);
                    il.MarkLabel(skipsetproperty);
                }
                //set loading flag
                il.Emit(OpCodes.Ldloc, loc_newobject);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Callvirt, type.GetProperty("IsLoading").GetSetMethod());

                //call OnAfterLoadFromDB()
                il.Emit(OpCodes.Ldloc, loc_newobject);
                il.Emit(OpCodes.Call, mth_OnAfterLoad);

                //load list onto the stack, clear changes and add the created object
                il.Emit(OpCodes.Ldloc, loc_objlist);
                il.Emit(OpCodes.Ldloc, loc_newobject);
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldfld, type.BaseType.GetField("Changes"));
                il.Emit(OpCodes.Callvirt, typeof(ChangeCollection).GetMethod("Clear"));
                il.Emit(OpCodes.Callvirt, typeof(List<object>).GetMethod("Add"));

                //add 1 to the index
                il.Emit(OpCodes.Ldloc, loc_index);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Stloc, loc_index);

                //entry point for first loop
                il.MarkLabel(enterloop);
                il.Emit(OpCodes.Ldloc, loc_index);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldlen);
                // if there are still items in the list loop again    
                il.Emit(OpCodes.Blt, loopagain);

                //load list onto stack and return
                il.Emit(OpCodes.Ldloc, loc_objlist);
                il.Emit(OpCodes.Ret);

                //Invoke and return
                object[] prms = { val };
                return (List<object>)Extract.Invoke(null, prms);

            }
            catch (Exception ex)
            {
                throw new Exception("Unable to convert the " + type.Name + " Dictionary structure into an object collection.", ex);
            }

        }

        protected bool TryRetrievePropertyFromDB<T>(out T? output, [CallerMemberName] string? property = null)
        {
            if (ID != Guid.Empty && !IsNew)
            {
                try
                {
                    throw new NotImplementedException();
                }
                catch (Exception) { }
            }
            output = default;
            return false;
        }
        [JsonIgnore]
        public bool HasChanges => HasChangedProperties;
        [JsonIgnore]
        protected internal bool IsLoading = false;

        public void ClearChanges()
        {
            if (ObjectSchema.HasLinkedChildObjects)
                foreach (var childCollection in ObjectSchema.LinkedChildObjectProperties!)
                    foreach (var childObj in (IEnumerable<ICoreObject>)childCollection.GetValue(this)!)
                        (childObj as ITFMHasChanges)?.ClearChanges();

            Changes.Clear();
        }


        [JsonIgnore]
        public ChangeCollection Changes;
        [JsonIgnore]
        public virtual bool DataChanged { get { return Changes.HasChanges; } }









        protected void UpdateProperty<T>(T value, [CallerMemberName] string property = null)
        {
            var prop = ObjectSchema.AllProperties.SingleOrDefault(x => x.Name == property);
            var implementsHasChanges = typeof(ITFMHasChanges).IsAssignableFrom(typeof(T));
            if (!IsLoading)
            {
                object oldvalue = null;
                if (value != null)
                    if (typeof(T) != typeof(byte[]))
                        oldvalue = prop.BackingField != null ?
                            prop.BackingField.GetValue(this) :
                            (!prop.RetrieveOnDemand && prop.CanRead) ?
                            prop.GetValue(this) : null;
                prop.BackingField?.SetValue(this, value);

                if (oldvalue == null && value == null)
                { }
                else if (oldvalue != null && value != null)
                {
                    if ((oldvalue == null && value != null) || (oldvalue != null && value == null) || (!oldvalue.Equals(value)))
                    {
                        Changes.Add(property, oldvalue, value, prop.SuppressLogging, prop.AbbreviatedLogging);
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
                    }
                }
                else if (typeof(T) == typeof(byte[]))
                {
                    Changes.Add(property, oldvalue, value, prop.SuppressLogging, prop.AbbreviatedLogging);
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
                }
                else if (value != null)
                {
                    if ((oldvalue == null && value != null))
                    {
                        Changes.Add(property, oldvalue, value, prop.SuppressLogging, prop.AbbreviatedLogging);
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
                    }
                }
            }
            else
            {
                prop.BackingField?.SetValue(this, value);
                if (implementsHasChanges)
                {
                    var e = new PropertyChangedEventArgs(property);
                    ((ITFMHasChanges)value).PropertyChanged += Base_PropertyChanged;
                }
            }
        }

        internal void ChildPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!IsLoading)
                PropertyChanged?.Invoke(sender, e);
        }

        private void Base_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            Changes.Add(((ITFMHasChanges)sender).MemberName, null, sender, true, false);
        }

    }

    [TypeConverter(typeof(NucleusTypeConverter))]
    public abstract class CoreObject<T> :  CoreObject, ICoreObject<T> where T : CoreObject<T>
    {
        public static IEnumerable<T> BindDataSet(DataSet dataSet)
        {
            if (dataSet.RowCount == 0)
                return [];
            var objList = new List<T>();
            for (int i = 0; i < dataSet.RowCount; i++)
            {
                T obj = Activator.CreateInstance<T>();
                var coreObj = obj as CoreObject;
                var row = dataSet[i];
                for (int ii = 0; ii < dataSet.ColumnCount; ii++)
                {
                    coreObj?.SetRawCurrentValue(dataSet.ColumnNames[ii], row[ii] ?? null);
                }
                (obj as CoreObject)!.IsLoading = false;
                objList.Add(obj);
            }
            return objList;
        }
    }

    public abstract class CoreChildObject<TParent> : CoreObject, ICoreObject, ICoreChildObject<TParent> where TParent : ICoreObject<TParent>
    {
        private TParent? _parent;
        
        [ParentObject]
        [DatabaseColumn]
        public TParent Parent { get => GetValue<TParent>() ?? throw new InvalidOperationException("Parent object is not bound."); }

        void ICoreChildObject<TParent>.BindParent(TParent parent) => BindParent(parent);
        protected internal void BindParent(TParent parent)
        {
            if(parent == null)
                throw new ArgumentNullException(nameof(parent));
            if (GetRawCurrentValue(nameof(Parent)) is TParent currentParent)
                throw new CoreObjectException(CoreObjectExceptionType.ParentObjectAlreadyBound);
            else
                SetValue(parent, property : nameof(Parent));
        }
    }

    public abstract class CoreChildObject<T, TParent> : CoreChildObject<TParent>, ICoreChildObject<T,TParent> 
        where T : ICoreObject<T> 
        where TParent : ICoreObject<TParent>
    {

    }


    public class CoreObjectException : Exception
    {
        public CoreObjectExceptionType ErrorType;
        public CoreObjectException(CoreObjectExceptionType type) : base($"{type}")
        {
            ErrorType = type;
        }
        public CoreObjectException(string message, CoreObjectExceptionType type) : base(message)
        {
            ErrorType = type;
        }
        public CoreObjectException(string message) : base(message)
        {
            ErrorType = CoreObjectExceptionType.NotDefined;
        }

    }

    public enum CoreObjectExceptionType
    {
        NoParentObjectDefined = 1,
        NoChildObjectsDefined = 2,
        ParentTypeMismatch = 3,
        ParentObjectAlreadyBound = 4,
        SetMethodCalledOutsideOfProperty = 5,
        GetMethodCalledOutsideOfProperty = 6,
        UnableToSaveFromChildObject = 50,
        NotDefined = 99,
    }
}
