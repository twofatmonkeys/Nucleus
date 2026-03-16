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

namespace TFM.Nucleus
{
    [TypeConverter(typeof(NucleusTypeConverter))]
    public abstract class CoreObject :
        ICoreObject, INotifyPropertyChanged, IGuidIdentifer
    {

        private readonly Dictionary<string, object?> _properties = [];
        private readonly Dictionary<string, object?> _newProperties = [];
        private bool _isValidated = false;


        public event PropertyChangedEventHandler? PropertyChanged;

        public bool IsNew => _isNew;
        private bool _isNew = true;
        public bool IsReadOnly => _isReadOnly;
        private bool _isReadOnly = false;

        public IDataProvider DataProvider => throw new NotImplementedException();

        [DatabaseColumnAttribute]
        public Guid ID
        {
            get { return GetValue<Guid>(); }
            //private set { SetValue(value); }
        }
        [DatabaseColumnAttribute(NonClusteredIndex = true, IndexSortDESC = true,
            SetValueOverride = eServerFunction.SYSDATETIMEOFFSET, ReturnOutputToProperty = true)]
        public DateTimeOffset Timestamp
        {
            get { return GetValue<DateTimeOffset>(); }
        }


        IObjectSchema ICoreObject.ObjectSchema => ObjectSchema;
        protected internal IObjectSchema ObjectSchema => Nucleus.ObjectSchema.Get(GetType());
        //ISqlSchema ICoreObject.SqlSchema => throw new NotImplementedException();
        //protected internal ISqlSchema SqlSchema => Core.SqlSchema.GetFromType(GetType());

        Dictionary<string, object?> ICoreObject.ChangedProperties => ChangedProperties;
        protected internal Dictionary<string, object?> ChangedProperties => IsNew ? _properties : _newProperties;

        protected internal bool HasChangedProperties => IsReadOnly ? false : ChangedProperties.Count > 0;


        void ICoreObject.BindParent(ICoreObject parentObj) => BindParent(parentObj);
        protected internal void BindParent(ICoreObject parentObj)
        {
            if (ObjectSchema.HasLinkedParentObject)
            {
                if (ObjectSchema.ParentProperty!.Type == parentObj.GetType())
                {
                    if (!_properties.TryGetValue(ObjectSchema.ParentProperty.Name, out _))
                        _properties[ObjectSchema.ParentProperty.Name] = parentObj;
                    else
                        throw new CoreObjectException(CoreObjectExceptionType.ParentObjectAlreadyBound);
                }
                else
                    throw new CoreObjectException(CoreObjectExceptionType.ParentTypeMismatch);
            }
            else
                throw new CoreObjectException(CoreObjectExceptionType.NoParentObjectDefined);
        }

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
            _isNew = false;
            //Cache.TryAddToCache(this);
            if (isRootObj)
                if (HasChangedLinkedObjects(out var objs))
                    foreach (CoreObject obj in objs!)
                        obj.CommitChanges(makeReadOnly, false);
        }

        void ICoreObject.ChildPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e) => ChildPropertyChanged(sender, e);
        protected internal void SetValue<T>(T? value, [CallerMemberName] string? property = null)
        {
            if (property == null)
                throw new CoreObjectException(CoreObjectExceptionType.SetMethodCalledOutsideOfProperty);
            if (IsReadOnly)
                throw new ReadOnlyException($"Unable to set the {property} property. {ObjectSchema.Name}({this}) is currently read only.");
            else
            {
                _isValidated = false;
                if (IsNew)
                {
                    _properties[property] = value;
                }
                else
                {
                    _newProperties[property] = value;
                }
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
            }
        }
        protected internal T? GetValue<T>([CallerMemberName] string? property = null)
        {
            if(property == null)
                throw new CoreObjectException(CoreObjectExceptionType.GetMethodCalledOutsideOfProperty);

            if (!IsReadOnly && !IsNew)
                if (_newProperties.TryGetValue(property, out var newValue))
                    return (T?)newValue;

            if (_properties.TryGetValue(property, out var value))
                return (T?)value;
            else
            {
                if (typeof(T).IsGenericType && typeof(T).GetGenericTypeDefinition() == typeof(ChildCollection<,>))
                    return (T?)ObjectSchema.GetProperty(property).Create(this);
                return default;
            }

        }
        //internal T GetValue<T>(string property)
        //{
        //    if (!IsReadOnly && !IsNew)
        //        if (_newProperties.TryGetValue(property, out var newValue))
        //            return (T)newValue;

        //    if (_properties.TryGetValue(property, out var value))
        //        return (T)value;
        //    else
        //        return default(T);

        //}

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

        private void LogChanges(ILogger? logger = null)
        {
            try
            {
                if (!ObjectSchema.SuppressLogging)
                {
                    var sb = new StringBuilder(IsNew ? $"Created {this}" : $"Updated {this}");
                    if (!IsNew)
                        sb.Append(_getChangeLogEntry());
                    logger?.LogInformation(sb.ToString());
                    //SystemLog.Write(sb.ToString(),
                    //    IsNew ? SystemLogType.Create : SystemLogType.Update, this);
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
                var sb = new StringBuilder($"Updated {this}");
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
                                    sb.Append($"cleared {value} from {prop.Key}");
                                else
                                    sb.Append($"updated {prop.Key} from {value}");
                            }
                        }
                    }
                }
                return sb.Append('.').ToString();
            }
            else
                return "";
        }


        //protected internal virtual void Instantiate()
        //{
        //    _properties = new Dictionary<string, object>();
        //}
        private static void Init<T>(out T assignTo, T value)
        {
            assignTo = value;
        }
        //protected internal void SetProperties(Dictionary<string, object> data)
        //{
        //    //_properties = data;
        //    IsNew = false;
        //}
        #region ICoreObject 

        T? ICoreObject.GetPropertyValue<T>(string propertyName) where T : default => GetValue<T>(propertyName);

        object? ICoreObject.GetPropertyValue(string propertyName) => GetRawCurrentValue(propertyName);

        void ICoreObject.BindValue(string property, object? value) => SetRawCurrentValue(property, value);

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


        void ICoreObject.OnOutputParameterRecieved(object? sender, IOutputReceivedEventArgs args) =>
        OnOutputParameterRecieved(sender, args);
        protected internal void OnOutputParameterRecieved(object? sender, IOutputReceivedEventArgs args)
        {
            _properties[args.PropertyName!] = args.Value!;
            if (sender is not null and IQueryOutputParameter)
            {
                ((IQueryOutputParameter)sender).OutputReceived -= OnOutputParameterRecieved;
            }
        }

        //public class ChangedPropertyCollection : IEnumerable<(string PropertyName, object PropertyValue)>
        //{
        //    private readonly Dictionary<string, object> properties = new Dictionary<string, object>();
        //    public Dictionary<string, object> ChangedProperties => properties;

        //    internal ChangedPropertyCollection(CoreObject obj)
        //    {
        //        if (obj.IsNew)
        //            properties = obj.properties;
        //        else
        //            properties = obj.newProperties;
        //    }

        //    public IEnumerator<(string PropertyName, object PropertyValue)> GetEnumerator() => 
        //        ChangedProperties.Select(o => Tuple.Create(o.Key, o.Value)).ToArray().GetEnumerator();
        //    IEnumerator IEnumerable.GetEnumerator()
        //    {
        //        throw new NotImplementedException();
        //    }
        //}




        [JsonIgnore]
        [SupressCopying]
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
        [SupressCopying]
        private IDataContext? _dataContext;


        private CoreObject[] _allLinkedObjects { get => throw new NotImplementedException(); }









        public string DisplayName { get { return this.ToString(); } }




        //Overwrite these properties in child classes to change behaviour
        protected internal virtual bool DisableLogging => false; //Setting true disables logging of actions within the Save() method of the object instance.
        protected internal virtual bool AllowCaching => true; //Setting true allows the object collection to be stored in the cache, false forces retrieval from the DB in every case
        protected internal virtual bool AllowDeletions => false;
        //protected internal virtual string SortBy => "";
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

















        //public static bool TryGetCurrent<T>(out T result) where T : ICoreObject, new()
        //{
        //    result = Current<T>();
        //    if (result != null)
        //        return true;
        //    else
        //        return false;
        //}
        //public static T Current<T>() where T : ICoreObject, new()
        //{
        //    Type t = typeof(T);
        //    try
        //    {
        //        var ID = Guid.Empty;
        //        if (App.IsHosted)
        //        {
        //            var objname = t.Name + "_ID";
        //            var objid = Session.Current(objname);
        //            if (objid != null)
        //                ID = (Guid)objid;
        //            else
        //                return default(T);
        //        }
        //        else
        //        {
        //            if (_CurrentCache.TryGetValue(t, out Guid _id))
        //                ID = _id;
        //            else
        //                return default(T);
        //        }
        //        return LoadByID<T>(ID);
        //    }
        //    catch (Exception ex)
        //    {
        //        var msg = $"Error retreiving Current instance of the {t.FullName} object. {ex.Message}";
        //        SystemLog.Write(msg, SystemLogType.Error);
        //        throw new Exception(msg, ex);
        //    }
        //}
        //protected static void SetCurrent<T>(T obj) where T : CoreObject, new()
        //{
        //    if (obj != null)
        //    {
        //        Type t = typeof(T);

        //        if (App.IsHosted)
        //        {
        //            var objname = t.Name + "_ID";
        //            Session.SetCurrent(objname, obj.ID);
        //        }
        //        else
        //        {
        //            _CurrentCache.AddOrUpdate(t, obj.ID, (key, oldvalue) => obj.ID);
        //        }
        //    }
        //}
        [SupressCopying]
        private static ConcurrentDictionary<Type, Guid> _CurrentCache = new ConcurrentDictionary<Type, Guid>();











        protected internal virtual void OnAfterLoadFromDB()
        {

        }
        protected internal virtual void OnAfterInitializeDB()
        {

        }

        //protected internal void _Validate()
        //{
        //    _isValidated = true;
        //    if(HasChangedLinkedObjects(out var objs))
        //        foreach(var obj in objs.Where(x=>!x._isValidated))
        //            obj.Validate();

        //}
        public virtual void Validate()
        {
            _isValidated = true;
            if (_ValidationRootFlag)
            {
                if (ObjectSchema.HasLinkedParentObject)
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
            var task = new Task(async () => await
                        SaveAsync());
            task.RunSynchronously();
        }

        public virtual Task SaveAsync() => SaveAsync(null);
        public virtual Task SaveAsync(IDataContext? dataContext) =>
            SaveAsync(dataContext, CancellationToken.None);
        public virtual Task SaveAsync(IDataContext? dataContext, CancellationToken token)
        {
            Validate();
            //check security
            return WriteToDBAsync(dataContext, token);
        }
        private async Task WriteToDBAsync(IDataContext? dataContext, CancellationToken token)
        {
            if (!_isValidated)
                throw new InvalidOperationException($"{this} is in an invalid state and cannot be saved. _Validate() method must be called from all overidden Validate() methods.");
            if (IsReadOnly)
                throw new Exception($"{this} is locked as read only.");
            try
            {
                if (HasChangedLinkedObjects(out var linkedObjects) || HasChangedProperties)
                    using (dataContext ??= await ContextGen.CreateContextAsync(true, token))
                        try
                        {
                            await dataContext.SaveAsync(this);
                            //await SqlSchema.BuildSaveDataQuery(this, linkedObjects).ExecuteAsync(dContext, token);
                            CommitChanges(true);
                        }
                        catch
                        {
                            await dataContext.TryRollbackAsync(token);
                            throw;
                        }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error writing to database: {ex.Message}", ex);
            }
        }
        public virtual void Save(IDataContext d)
        {
            var task = new Task(async () => await
                SaveAsync(d).ConfigureAwait(false));
            task.RunSynchronously();

            //try
            //{

            //    MethodInfo method = this.GetType().GetMethod("PreSave");
            //    method.Invoke(this, null);
            //}
            //catch (TargetInvocationException tex)
            //{
            //    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tex.InnerException).Throw();
            //    throw;
            //}
            //PreSave();
            //try
            //{
            //    if (d != null)
            //        WriteToDB(d);
            //    else
            //        using (var db = new Context(d))
            //            WriteToDB(db);
            //}
            //catch (Exception ex)
            //{
            //    throw new Exception($"Unable to save {this} to the database. {ex.Message}", ex);
            //}
        }
        private void WriteToDB(IDataContext db)
        {
            throw new NotImplementedException();
            //if (DataChanged)
            //{
            //    try
            //    {
            //        data_Context = db;
            //        try
            //        {
            //            var query = ObjectSchema.SqlSchema.BuildSaveDataQuery(null);
            //            foreach (var col in ObjectSchema.AllDBColumns)
            //                if (!IsNew && col.Name == "ID")
            //                    query.Where.And(col.Name, col.GetDBValue(this));
            //                else
            //                    query.SetParameters.Add(col.Name, col.GetDBValue(this));
            //            query.Type = IsNew ? QueryType.INSERT : QueryType.UPDATE;
            //            db.ExecuteSQL(query);
            //            if (!DisableLogging)
            //                SystemLog.Write($"{(IsNew ? "Created new" : "Updated")} {ObjectSchema.Name}, {this}{(IsNew ? "" : $" {Changes}")}", SystemLogType.CreateOrUpdate, this);


            //            //Save any Enumerable Child Properties
            //            if (ObjectSchema.HasLinkedChildObjects)
            //            {
            //                foreach (var child in ObjectSchema.LinkedChildObjectProperties)
            //                    foreach (CoreObject b in (IEnumerable)child.GetValue(this))
            //                        //(child.ChildSchema.TrySetParentID(ID, b) as CoreObject).WriteToDB(db);
            //                        if (!IsNew)
            //                            PurgeDeletedChildren(db);
            //            }
            //            //IsNew = false;
            //            ClearChanges();
            //            Cache.TryAddToCache(this);
            //        }
            //        catch (Exception)
            //        {
            //            db.Rollback();
            //            throw;
            //        }
            //        //}
            //    }
            //    catch (Exception ex)
            //    {
            //        SystemLog.Write($"Error writing to database: {ex.Message}", SystemLogType.Error, this);
            //        throw new Exception($"Error writing to database: {ex.Message}", ex);
            //    }
            //}
        }







        private void PurgeDeletedChildren(IDataContext da)
        {
            throw new NotImplementedException();
            //using (var db = new Context(da))
            //{
            //    try
            //    {
            //        foreach (var child in ObjectSchema.LinkedChildObjectProperties)
            //        {
            //            var parms = new ParameterCollection(){
            //                new Parameter("guidArray", ((ChildCollection<CoreObject,CoreObject>)child.GetValue(this)).Select(x => x.ID).ToArray()),
            //                new Parameter("ParentID", ID)
            //            };
            //            var sql = $"DELETE FROM {child.ChildSchema.SqlSchema.TableName} WHERE {child.ParentIDPropertyName} = @ParentID AND ID NOT IN @guidArray";
            //            da.RunQuery(sql, parms);
            //        }
            //    }
            //    catch (Exception ex)
            //    {
            //        db.Rollback();
            //        throw new Exception($"Error purging deleted child objects. {ex.Message}", ex);
            //    }
            //}
        }
        //public static void BulkSave<T>(IEnumerable<T> objs) where T : CoreObject, new()
        //{
        //    BulkSave(objs, null);
        //}
        public static void BulkSave<TObject>(IEnumerable<TObject> objs, IDataContext? dc = null) where TObject : CoreObject, new()
        {
            throw new NotImplementedException();
            //try
            //{
            //    if (objs.Count() < 11)
            //    {
            //        using (var db = ContextGen.CreateContext(true, dc))
            //        {
            //            try
            //            {
            //                foreach (var o in objs)
            //                    o.Save(db);
            //                return;
            //            }
            //            catch (Exception ex)
            //            {
            //                db.Rollback();
            //                throw new Exception($"Unable to bulk save {ObjectSchema<T>.Get()} collection to the database. {ex.Message}", ex);
            //            }
            //        }
            //    }


            //    //var objectSchema = ObjectSchema.GetObjectSchema<T>();
            //    //string[] columnNames = SqlSchema.GetDBColumnNames(typeof(T));
            //    //var tablename = SqlSchema.TableName(typeof(T));

            //    //var updateSQL = new List<char>();
            //    //var updateParams = new DataAccess.DBParameterList();
            //    //var updateArray = new ConcurrentBag<(string, DataAccess.DBParameterList)>();

            //    //var _logsource = App.AppName;
            //    //var _logclientdata = Helpers.GetClientData();
            //    //var _loguserid = Security.CurrentUser?.ID;
            //    //var _linkobject = (typeof(T)).FullName;
            //    //var _timestamp = DataAccess.GetServerDateTimeOffset();
            //    //var _sw = Stopwatch.StartNew();
            //    ////create datatable schema
            //    //var templateDT = new DataTable();
            //    ////TODO sql type matching for complex objects
            //    //foreach (var colName in columnNames.Where(x => x != "ClusterID"))
            //    //    templateDT.Columns.Add(colName, objectSchema.Properties.Single(x => x.Name == colName).PropertyType);
            //    ////build datatables - update & insert
            //    //var insertDT = templateDT.Copy();
            //    //var updateDT = templateDT.Copy();
            //    //var logDT = SystemLog.GetDataTableSchema();
            //    //var linkDT = SystemLog.Link.GetDataTableSchema();
            //    //foreach (var o in objs.Where(x=>x.HasChanges))
            //    //{
            //    //    if (o == null)
            //    //        break;
            //    //    o.PreSave();
            //    //    DataRow row = o.IsNew ? insertDT.NewRow() : updateDT.NewRow();
            //    //    foreach (var os in objectSchema.Properties)
            //    //    {
            //    //        if (columnNames.Contains(os.Name))
            //    //        {
            //    //            if (Attribute.IsDefined(os, typeof(EnumerableChildProperty)))
            //    //                throw new Exception("Object cannot be bulk saved, it has child objects that must be saved");
            //    //            if (os.Name != "Timestamp")
            //    //            {
            //    //                object obj = os.GetValue(o);
            //    //                if (obj != null)
            //    //                    row[os.Name] = obj;
            //    //                else
            //    //                    row[os.Name] = DBNull.Value;
            //    //            }
            //    //            else
            //    //            {
            //    //                row[os.Name] = _timestamp.AddTicks(_sw.ElapsedTicks);
            //    //            }
            //    //        }

            //    //    }
            //    //    if (o.IsNew)
            //    //        insertDT.Rows.Add(row);
            //    //    else if(o.HasChanges)
            //    //        updateDT.Rows.Add(row);
            //    //    //add log entries
            //    //    if (!o.DisableLogging && !String.IsNullOrEmpty(o.Changes.ToString()))
            //    //    {
            //    //        var logentry = "";
            //    //        if (o.IsNew)
            //    //            logentry = $"Created new {o.ObjectName}, {o.ToString()}.";
            //    //        else
            //    //            logentry = $"Updated {o.ObjectName}, {o.ToString()}. {o.Changes.ToString()}";
            //    //        var tempLog = SystemLog.CreateDatatable(logentry, SystemLog.LogType.CreateOrUpdate, _logclientdata, new object[] { o }, _loguserid,_timestamp.AddTicks(_sw.ElapsedTicks));
            //    //        logDT.Merge(tempLog.dtLog);
            //    //        linkDT.Merge(tempLog.dtLinks);
            //    //    }
            //    //}

            //    var bulkSaveData = ExtractBulkSaveData(objs);
            //    using (Context d = new Context(da))
            //    {
            //        foreach (var data in bulkSaveData)
            //        {

            //            if (data.UpdateData.Rows.Count > 0 || data.InsertData.Rows.Count > 0)
            //            {
            //                //write to DB

            //                try
            //                {
            //                    if (data.UpdateData.Rows.Count > 0)
            //                    {
            //                        //create temp table
            //                        var _tempTable = $"tmp_{ThreadSafeRandom.GetRandomString(8)}";
            //                        d.ExecuteSQL($"SELECT TOP 0 * INTO {_tempTable} FROM {data.TableName}");
            //                        //insert
            //                        d.BulkInsert(_tempTable, data.UpdateData);
            //                        //merge
            //                        var _updateQuery = $"UPDATE {data.TableName} SET ";
            //                        foreach (var colName in data.ColumnNames.Where(x => x != "ClusterID"))
            //                            _updateQuery += $"{data.TableName}.{colName} = {_tempTable}.{colName}, ";
            //                        _updateQuery = _updateQuery.RemoveFromEnd(2);
            //                        _updateQuery += $" FROM {_tempTable}, {data.TableName} WHERE {_tempTable}.ID = {data.TableName}.ID";
            //                        d.ExecuteSQL(_updateQuery);
            //                        //delete
            //                        d.ExecuteSQL($"DROP TABLE {_tempTable}");
            //                    }

            //                    if (data.InsertData.Rows.Count > 0)
            //                        d.BulkInsert(data.TableName, data.InsertData);
            //                    //if (data.LogData.Rows.Count > 0)
            //                    //    d.BulkInsert(SqlSchema<SystemLog>.Get().TableName, data.LogData);
            //                    //if (data.LinkData.Rows.Count > 0)
            //                    //    d.BulkInsert(SqlSchema<SystemLog.Link>.Get().TableName, data.LinkData);

            //                }
            //                catch (Exception)
            //                {
            //                    d.Rollback();
            //                    throw;
            //                }
            //            }
            //            Cache<T>.ClearCache();

            //        }
            //    }
            //}
            //catch (Exception ex)
            //{
            //    var err = $"Error bulk saving {typeof(T).FullName} collection to DB. " + ex.Message;
            //    SystemLog.Write(err);
            //    throw new Exception(err, ex);
            //}
        }

        private static List<BulkSaveDataSet> ExtractBulkSaveData(IEnumerable<ICoreObject?> objs)
        {
            throw new NotImplementedException();
            //var type = objs.Cast<object>().First().GetType();

            //var bulkData = new BulkSaveDataSet(type);
            //var _timestamp = ServerFunction.SysDateTimeOffset.GetValue();
            //var _sw = Stopwatch.StartNew();
            //var _logsource = App.AppName;
            //var _logclientdata = Helpers.GetClientData();
            //var _loguserid = Security.CurrentUser?.ID;
            //var _childObjects = new Dictionary<Type, List<object>>();
            //var _objectSchema = Core.ObjectSchema.GetFromType(type);
            ////(ObjectSchema ObjectSchema, PropertyInfo BoundPropertyInfo)[] _childObjectPInfo = null;
            //if (_objectSchema.HasLinkedChildObjects)
            //    foreach (var childCollectionSchema in _objectSchema.LinkedChildObjectProperties)
            //        _childObjects[childCollectionSchema.ChildSchema.Type] = new List<object>();

            //foreach (var o in objs.Cast<CoreObject>().Where(x => x.HasChanges))
            //{
            //    if (o == null)
            //        break;
            //    o.PreSave();
            //    DataRow row = o.IsNew ? bulkData.InsertData.NewRow() : bulkData.UpdateData.NewRow();
            //    foreach (var os in o.ObjectSchema.AllDBColumns)
            //        row[os.Name] = os.Name != "Timestamp" ? os.GetDBValue(o) : _timestamp;

            //    if (_objectSchema.HasLinkedChildObjects)
            //    {
            //        foreach (var childCollectionSchema in _objectSchema.LinkedChildObjectProperties)
            //        {
            //            var childCollection = (IEnumerable<object>)childCollectionSchema.GetValue(o);
            //            if (childCollection != null)
            //            {
            //                foreach (var child in childCollection)
            //                    //childCollectionSchema.ChildSchema.TrySetParentID(o.ID, child);
            //                    if (childCollection.Count() > 0)
            //                        _childObjects[childCollectionSchema.ChildSchema.Type].AddRange(childCollection.Cast<object>());
            //            }
            //        }
            //    }
            //    if (o.IsNew)
            //        bulkData.InsertData.Rows.Add(row);
            //    else if (o.HasChanges)
            //        bulkData.UpdateData.Rows.Add(row);
            //    //add log entries
            //    //if (!o.DisableLogging && !string.IsNullOrEmpty(o.Changes.ToString()))
            //    //{
            //    //    var logentry = "";
            //    //    if (o.IsNew)
            //    //        logentry = $"Created new {o.ObjectSchema.Name}, {o.ToString()}.";
            //    //    else
            //    //        logentry = $"Updated {o.ObjectSchema.Name}, {o}. {o.Changes}";
            //    //    var tempLog = SystemLog.CreateDatatable(logentry, SystemLog.SystemLogType.CreateOrUpdate, _logclientdata, new object[] { o }, _loguserid, _timestamp.AddTicks(_sw.ElapsedTicks));
            //    //    bulkData.LogData.Merge(tempLog.dtLog);
            //    //    bulkData.LinkData.Merge(tempLog.dtLinks);
            //    //}
            //}

            //var data = new List<BulkSaveDataSet>() { bulkData };
            //if (_childObjects.Count > 0)
            //    foreach (var o in _childObjects)
            //        data.AddRange(ExtractBulkSaveData(o.Value));


            //return data;
        }






















        internal class BulkSaveDataSet
        {
            internal DataTable? InsertData;
            internal DataTable? UpdateData;
            internal DataTable? LogData;
            internal DataTable? LinkData;
            internal string[] ColumnNames;
            internal string TableName;


            internal BulkSaveDataSet(Type type)
            {
                throw new NotImplementedException();
                //var obj = TFM.Nucleus.ObjectSchema.GetFromType(type);
                //ColumnNames = obj.AllDBColumns.Select(x => x.Name).ToArray();
                //TableName = obj.SqlSchema.TableName;
                //var templateDT = new DataTable();
                ////TODO sql type matching for complex objects
                //foreach (var col in obj.AllDBColumns)
                //    templateDT.Columns.Add(col.Name, col.DBWriteType);
                ////build datatables - update & insert
                //InsertData = templateDT.Copy();
                //UpdateData = templateDT.Copy();
                ////LogData = SystemLog.GetDataTableSchema();
                ////LinkData = SystemLog.Link.GetDataTableSchema();
            }
        }







        public void Delete()
        {
            Delete(null);
        }
        public void Delete(IDataContext? d)
        {
            if (this.AllowDeletions)
            {
                try
                {
                    using (var db = ContextGen.CreateContext(true, d))
                    {
                        try
                        {
                            //var dObj = new DeletedObject(this);
                            //dObj.Save(db);
                            db.Delete(this);
                            //var dbp = new Parameter("ID", this.ID);
                            //db.RunQuery($"DELETE from {ObjectSchema.SqlSchema.TableName} WHERE {dbp.LeftOperand} = {dbp.RightOperand}", dbp);
                            SystemLog.Write($"Deleted {this} from the database", this);
                            Cache.TryDeleteFromCache(this, 10);

                        }
                        catch (Exception)
                        {
                            db.Rollback();
                            throw;
                        }
                    }
                }
                catch (Exception ex)
                {
                    var err = $"Error deleting {this.ToString()} from DB. " + ex.Message;
                    SystemLog.Write(err);

                    throw new Exception(err, ex);
                }
            }
            else
                throw new Exception($"Unable to delete {this}. Deletion is not permitted for {ObjectSchema} types.");
        }

        //public static object LoadByID(string typename, Guid id)
        //{
        //    //return ObjectSchema.TryGetByTypeName(typename, out var objectSchema) ? LoadByID(objectSchema.Type, id) : null;
        //}
        //public static object LoadByID(Type type, Guid id)
        //{
        //    return id != Guid.Empty ? Cache.TryLoadFromCache(type, id, out object val) ? val : LoadByIDFromDB(type, id) : null;
        //}

















        public static T LoadByID<T>(Guid id) where T : ICoreObject
        {
            throw new NotImplementedException();
            //return id != Guid.Empty ? (Cache<T>.TryGetObjectByID(id, out T val) ? val : LoadByIDFromDB<T>(id)) : default;
        }
        //public static object LoadByIDFromDB(string typename, Guid id)
        //{
        //    //return id != Guid.Empty ? ObjectSchema.TryGetByTypeName(typename, out var objectSchema) ? LoadByIDFromDB(objectSchema.Type, id) : null : null;
        //}
        public static T? LoadByIDFromDB<T>(Guid id) where T : CoreObject, new()
        {
            return id != Guid.Empty ? LoadObjectFromDBWhere<T>(x => ((CoreObject)(object)x).ID == id) : default;
        }
        //public static object LoadByIDFromDB(Type type, Guid id)
        //{
        //    if (id == Guid.Empty)
        //        return null;
        //    try
        //    {
        //        //return LoadFromDBWhere()
        //        object x = Activator.CreateInstance(type);
        //        using (var db = new Context())
        //        {
        //            var query = ((Base)x).GetSqlSchema().GetDefaultSelectQuery();
        //            query.Where.And(new Parameter("ID", id));
        //            using (var dt = db.GetData(query))
        //            {
        //                if (dt.Rows.Count > 0)
        //                { ((Base)x).DataRowToClass(dt.Rows[0]); return x; }
        //                else
        //                { return null; }
        //            }
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        throw new Exception("Unable to load " + type.Name + " object with ID:" + id.ToString() + " from the database. " + ex.Message);
        //    }
        //}
        public static bool TryLoadByID<T>(Guid id, out T obj) where T : CoreObject, ICoreObject, new()
        {
            obj = LoadByID<T>(id);
            return (obj != null);
        }
        public static T? LoadObjectWhere<T>(Expression<Func<T, bool>> expression) where T : CoreObject, new()
        {
            if (expression != null)
            {
                if (typeof(T).IsCacheable())
                {
                    var list = LoadWhere(expression);
                    if (list.Count > 0)
                        return (T)list[0];
                }
                return LoadObjectFromDBWhere(expression);
            }
            return default;
        }
        public static T? LoadObjectFromDBWhere<T>(Expression<Func<T, bool>> expression) where T : CoreObject, new()
        {
            if (expression != null)
            {
                var list = LoadFromDBWhere(expression);
                if (list.Count > 0)
                    return (T)list[0];
            }
            return default(T);
        }
        public static T? LoadObjectFromDBWhere<T>(Expression<Func<T, bool>> expression, List<(string, PropertyInfo)> orderby) where T : CoreObject, new()
        {
            if (expression != null)
            {
                var list = LoadFromDBWhere(expression, 1, orderby);
                if (list.Count > 0)
                    return (T)list[0];
            }
            return default;
        }
        public static List<T> LoadAll<T>() where T : ICoreObject, new()
        {
            throw new NotImplementedException();
            //if (Cache<T>.TryGetObjects(out var obj))
            //    return obj;
            //else
            //    return LoadAllFromDB<T>();
        }

        //public static List<object> LoadAll(string typename)
        //{
        //    return ObjectSchema.TryGetObjectType(typename, out Type x) ? LoadAll(x) : new List<object>();
        //}
        //public static List<object> LoadAll(Type type)
        //{
        //    var res = Cache.TryLoadFromCache(type).Result;
        //    if (res.IsSuccess)
        //        return res.Result;
        //    return LoadAllFromDB(type);
        //}
        public static List<T> LoadAllFromDB<T>() where T : CoreObject, new() =>
            LoadFromDBWhere<T>(null);
        public static List<T> LoadWhere<T>(Expression<Func<T, bool>> expression) where T : CoreObject, new() =>
            LoadWhere(expression, 0);
        public static List<T> LoadWhere<T>(Expression<Func<T, bool>> expression, int take) where T : CoreObject, new() =>
            _LoadWhere(expression, take);
        protected static List<T> _LoadWhere<T>(Expression<Func<T, bool>> expression, int take) where T : CoreObject, new()
        {
            throw new NotImplementedException();
            //if (typeof(T).IsInterface)
            //{
            //    var l = new List<T>();
            //    foreach (var p in Nucleus.ObjectSchema.GetWhereImplements(typeof(T)))
            //    {
            //        try
            //        {
            //            var expMethod = typeof(ExpressionExtensions).GetMethod(nameof(ExpressionExtensions.ChangeTypeOfT), BindingFlags.Public | BindingFlags.Static).MakeGenericMethod(new Type[] { typeof(T), p.Type });
            //            var loadMethod = typeof(CoreObject).GetMethod(nameof(CoreObject._LoadWhere), BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(p.Type);

            //            object[] prms = { expression != null ? expMethod.Invoke(null, new object[] { expression }) : null, 0 };
            //            var s = loadMethod.Invoke(null, prms);
            //            l.AddRange((s as IEnumerable<T>));
            //        }
            //        catch (TargetInvocationException te)
            //        {
            //            throw te.InnerException;
            //        }
            //        catch (Exception)
            //        {
            //            throw;
            //        }
            //    }
            //    return l;
            //}
            //else
            //{
            //    throw new NotImplementedException();
            //    //if (Cache<T>.TryGetObjects(out var result))
            //    //{
            //    //    if (take < 1)
            //    //        return result.Where(expression.Compile()).ToList();
            //    //    else
            //    //        return result.Where(expression.Compile()).Take(take).ToList();
            //    //}
            //    //return LoadFromDBWhere<T>(expression, take);
            //}
        }
        public static List<TObject> LoadFromDBWhere<TObject>(Expression<Func<TObject, bool>>? expression) where TObject : ICoreObject, new() =>
            LoadFromDBWhere<TObject>(expression, 0, null);
        public static List<TObject> LoadFromDBWhere<TObject>(Expression<Func<TObject, bool>>? expression, int take) where TObject : ICoreObject, new() =>
            LoadFromDBWhere<TObject>(expression, take, null);
        public static List<TObject> LoadFromDBWhere<TObject>(Expression<Func<TObject, bool>>? expression, int take, List<(string, PropertyInfo)>? orderby) where TObject : ICoreObject, new() =>
            _LoadFromDBWhere<TObject>(expression, take, orderby);
        private static List<TObject> _LoadFromDBWhere<TObject>(Expression<Func<TObject, bool>>? expression, int take, List<(string, PropertyInfo)>? orderby) where TObject : ICoreObject, new()
        {
            throw new NotImplementedException();
            //if (typeof(T).IsInterface)
            //{
            //    var l = new List<T?>();
            //    foreach (var p in TFM.Nucleus.ObjectSchema.GetWhereImplements(typeof(T)))
            //    {
            //        try
            //        {
            //            var expMethod = typeof(ExpressionExtensions).GetMethod(nameof(ExpressionExtensions.ChangeTypeOfT), BindingFlags.Public | BindingFlags.Static)!.MakeGenericMethod([typeof(T), p.Type]);
            //            var loadMethod = typeof(CoreObject).GetMethod(nameof(CoreObject._LoadFromDBWhere), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(p.Type);
            //            object?[] prms = { expression != null ? expMethod.Invoke(null, [expression]) : null, 0, orderby };
            //            var s = loadMethod.Invoke(null, prms);
            //            l.AddRange((s as IEnumerable<T>)!);
            //        }
            //        catch (TargetInvocationException te)
            //        {
            //            throw te.InnerException ?? new Exception("Unable to load collection");
            //        }
            //    }
            //    return l;
            //}
            //else
            //{
            //    try
            //    {
            //        var sql = ParseExpression(expression?.Body);
            //        if (ObjectSchema<T>.Get().HasLinkedChildObjects)
            //        {
            //            var agg = SqlSchema<T>.Get().BuildAggregrateQueryString(sql.where, take, orderby);
            //            StringBuilder s = new StringBuilder();
            //            foreach (var n in agg.Flatten())
            //                s.Append(n.Value.QueryString).Append("; ");
            //            using (var d = new Context())
            //            {
            //                using (var ds = d.RunAggregrateQuery(s.ToString(), sql.parms))
            //                {
            //                    if (ds.Tables.Count > 0 && ds.Tables[0].Rows.Count > 0)
            //                    {
            //                        var data = new Dictionary<Type, List<object>>();
            //                        var parent = BuildObjectCollection<T>(CreateDictionaryFromDT<T>(ds.Tables[0]));
            //                        data.Add(typeof(T), parent.Cast<object>().ToList());
            //                        for (int i = 1; i < ds.Tables.Count; i++)
            //                        {
            //                            var def = agg.FlattenToArray()[i];
            //                            data.Add(def.Value.Type, BuildObjectCollection(CreateDictionaryFromDT(ds.Tables[i], def.Value.Type), def.Value.Type));
            //                        }
            //                        foreach (var n in agg.Flatten())
            //                        {
            //                            if (n.HasParent)
            //                                BindChildObjects(data.GetValueOrDefault(n.Parent.Value.Type), data.GetValueOrDefault(n.Value.Type));
            //                        }
            //                        return parent;
            //                    }
            //                    return new List<T>();
            //                }
            //            }
            //        }
            //        else
            //            using (var d = new Context())
            //            {
            //                if (sql.expression == null)
            //                {
            //                    using (DataTable dt = d.RunQuery(SqlSchema<T>.Get().GetQueryString(sql.where, take, orderby), sql.parms))
            //                    {
            //                        return dt.Rows.Count > 0 ? BuildObjectCollection<T>(CreateDictionaryFromDT<T>(dt)) : new List<T>();
            //                    }
            //                }
            //                else
            //                {
            //                    using (DataTable dt = d.RunQuery(SqlSchema<T>.Get().GetQueryString(sql.where), sql.parms))
            //                    {
            //                        var coll = dt.Rows.Count > 0 ? BuildObjectCollection<T>(CreateDictionaryFromDT<T>(dt)) : new List<T>();
            //                        var res = coll.Where(Expression.Lambda<Func<T, bool>>(sql.expression, expression.Parameters).Compile());
            //                        if (orderby.Count > 0)
            //                            throw new NotImplementedException();
            //                        if (take > 0)
            //                            res = res.Take(take);
            //                        return res.ToList();
            //                    }
            //                }
            //            }
            //    }
            //    catch (Exception ex)
            //    {
            //        string err = "Unable to load the " + typeof(T).FullName + " object collection from the database. " + ex.Message;
            //        throw new Exception(err, ex);
            //    }
            //}
        }

        private async static Task<List<T>> _LoadFromDBWhereAsync<T>(Expression<Func<T, bool>> expression, int take, List<(string, PropertyInfo)> orderby) where T : CoreObject, new()
        {
            await Task.Yield();
            throw new NotImplementedException();
            //if (typeof(T).IsInterface)
            //{
            //    var l = new List<T>();
            //    foreach (var p in Core.ObjectSchema.GetWhereImplements(typeof(T)))
            //    {
            //        try
            //        {
            //            var expMethod = typeof(ExpressionExtensions).GetMethod(nameof(ExpressionExtensions.ChangeTypeOfT), BindingFlags.Public | BindingFlags.Static).MakeGenericMethod(new Type[] { typeof(T), p.Type });
            //            var loadMethod = typeof(CoreObject).GetMethod(nameof(CoreObject._LoadFromDBWhere), BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(p.Type);
            //            object[] prms = { expression != null ? expMethod.Invoke(null, new object[] { expression }) : null, 0, orderby };
            //            var s = loadMethod.Invoke(null, prms);
            //            l.AddRange((s as IEnumerable<T>));
            //        }
            //        catch (TargetInvocationException te)
            //        {
            //            throw te.InnerException;
            //        }
            //    }
            //    return l;
            //}
            //else
            //{
            //    try
            //    {
            //        var sql = ParseExpression(expression?.Body);
            //        if (ObjectSchema<T>.Get().HasLinkedChildObjects)
            //        {
            //            var agg = SqlSchema<T>.Get().BuildAggregrateQueryString(sql.where, take, orderby);
            //            StringBuilder s = new StringBuilder();
            //            foreach (var n in agg.Flatten())
            //                s.Append(n.Value.QueryString).Append("; ");
            //            using (var d = await Context.InitialiseAsync())
            //            {
            //                using (var ds = await d.OldRunAggregrateQueryAsync(s.ToString(), sql.parms))
            //                {
            //                    if (ds.Tables.Count > 0 && ds.Tables[0].Rows.Count > 0)
            //                    {
            //                        var data = new Dictionary<Type, List<object>>();
            //                        var parent = BuildObjectCollection<T>(CreateDictionaryFromDT<T>(ds.Tables[0]));
            //                        data.Add(typeof(T), parent.Cast<object>().ToList());
            //                        for (int i = 1; i < ds.Tables.Count; i++)
            //                        {
            //                            var def = agg.FlattenToArray()[i];
            //                            data.Add(def.Value.Type, BuildObjectCollection(CreateDictionaryFromDT(ds.Tables[i], def.Value.Type), def.Value.Type));
            //                        }
            //                        foreach (var n in agg.Flatten())
            //                        {
            //                            if (n.HasParent)
            //                                BindChildObjects(data.GetValueOrDefault(n.Parent.Value.Type), data.GetValueOrDefault(n.Value.Type));
            //                        }
            //                        return parent;
            //                    }
            //                    return new List<T>();
            //                }
            //            }
            //        }
            //        else
            //            using (var d = new Context())
            //            {
            //                if (sql.expression == null)
            //                {
            //                    using (DataTable dt = d.RunQuery(SqlSchema<T>.Get().GetQueryString(sql.where, take, orderby), sql.parms))
            //                    {
            //                        return dt.Rows.Count > 0 ? BuildObjectCollection<T>(CreateDictionaryFromDT<T>(dt)) : new List<T>();
            //                    }
            //                }
            //                else
            //                {
            //                    using (DataTable dt = d.RunQuery(SqlSchema<T>.Get().GetQueryString(sql.where), sql.parms))
            //                    {
            //                        var coll = dt.Rows.Count > 0 ? BuildObjectCollection<T>(CreateDictionaryFromDT<T>(dt)) : new List<T>();
            //                        var res = coll.Where(Expression.Lambda<Func<T, bool>>(sql.expression, expression.Parameters).Compile());
            //                        if (orderby.Count > 0)
            //                            throw new NotImplementedException();
            //                        if (take > 0)
            //                            res = res.Take(take);
            //                        return res.ToList();
            //                    }
            //                }
            //            }
            //    }
            //    catch (Exception ex)
            //    {
            //        string err = "Unable to load the " + typeof(T).FullName + " object collection from the database. " + ex.Message;
            //        throw new Exception(err, ex);
            //    }
            //}
        }
        //public static List<object> LoadFromDBWhere(Type type, Expression<Func<Base, bool>> expression)
        //{
        //    return LoadFromDBWhere(type, expression,0,null);
        //    //try
        //    //{
        //    //    var sql = ParseExpression(expression.Body);
        //    //    using (var d = new DataAccess())
        //    //    {
        //    //        using (DataTable dt = d.RunQuery(SqlSchema.GetQueryString(type, sql.where), sql.parms))
        //    //        {
        //    //            if (dt.Rows.Count > 0)
        //    //            {
        //    //                return ExtractCollection(CreateDictionaryFromDT(dt, type), type);
        //    //            }
        //    //            else { return new List<object>(); }
        //    //        }
        //    //    }
        //    //}
        //    //catch (Exception ex)
        //    //{
        //    //    string err = "Unable to load the " + type.FullName + " object collection from the database. " + ex.Message;
        //    //    throw new Exception(err, ex);
        //    //}
        //}
        //public static List<object> LoadFromDBWhere(Type type, Expression<Func<Base, bool>> expression, int take, List<(string, PropertyInfo)> orderby)
        //{
        //    try
        //    {
        //        try
        //        {
        //            var newExp = expression?.ChangeTypeOfT<User>();
        //            var mi = typeof(Base).GetMethod("LoadFromDBWhere_root", BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(type);
        //            object[] prms = { expression?.ChangeTypeOfT<Base, User>(), take, orderby };
        //            var s = mi.Invoke(null, prms);
        //            return ((IList)s).Cast<object>().ToList();
        //        }
        //        catch (TargetInvocationException te)
        //        {
        //            throw te.InnerException;
        //        }

        //        //    var sql = ParseExpression(expression);
        //        //    using (var d = new DataAccess())
        //        //    {
        //        //        using (DataTable dt = d.RunQuery(SqlSchema.GetQueryString(type, sql.where,take,orderby), sql.parms))
        //        //        {
        //        //            return dt.Rows.Count > 0 ? ExtractCollection(CreateDictionaryFromDT(dt, type), type) : new List<object>();
        //        //            //return ObjectSchema.GetObjectSchema(type).HasEnumerableChildProperties ?
        //        //                //RetrieveAndAttachChildObjects(coll, type) : coll;
        //        //        }
        //        //    }
        //    }
        //    catch (Exception ex)
        //    {
        //        string err = "Unable to load the " + type.FullName + " object collection from the database. " + ex.Message;
        //        throw new Exception(err, ex);
        //    }
        //}


        //private static List<object> RetrieveAndAttachChildObjects(List<object> parentObjs, Type type)
        //{
        //    var parents = new Dictionary<Guid, object>();
        //    foreach (var o in parentObjs)
        //        parents.Add(((Base)o).ID, o);
        //    foreach (var p in ObjectSchema.GetObjectSchema(type).EnumerableChildProperties)
        //    {
        //        var childObjs = LoadChildObjectsFromDB(parents, p.ObjectSchema);

        //        Parallel.ForEach(parentObjs.Cast<Base>(), o =>
        //            {
        //                var filterMethod = typeof(TFM.Core.ListExtensions).GetMethod("FilterByGuid").MakeGenericMethod(p.ObjectSchema.Type);
        //                p.BoundPropertyInfo.SetValue(o, filterMethod.Invoke(null, new object[] { childObjs, o.ID, p.ObjectSchema.ParentIDPropertyName }));
        //                o.ClearChanges();
        //            });

        //    }
        //    return parentObjs;
        //}
        //private static Dictionary<string,object>[] LoadChildObjectsFromDB(Guid[] parentIDs, ObjectSchema childProperty)
        //{
        //    using (var d = new DataAccess())
        //    {
        //        var param = new DataAccess.DBParameter("idArray", parentIDs.ToDelimitedString(","));
        //        var where = $"WHERE {childProperty.ParentIDPropertyName} IN (SELECT convert(uniqueidentifier, value) FROM fn_split({param.VariableName}, ','))";
        //        using (DataTable dt = d.RunQuery(SqlSchema.GetQueryString(childProperty.Type, where), param))
        //        {
        //            return dt.Rows.Count > 0 ?  CreateDictionaryFromDT(dt, childProperty.Type): null;
        //            //return dt.Rows.Count > 0 ? ExtractCollectionWithType(CreateDictionaryFromDT(dt, childProperty.Type), childProperty.Type) : new List<object>();
        //        }
        //    }
        //}
        //private void LinkChildObjects(IEnumerable<object> childObjs, PropertyInfo parentIDProperty, Prop)
        //{ 

        //}
        //private static (string where, ParameterCollection? parms, Expression? expression) ParseExpression(Expression? expression)
        //{
        //    return ParseExpression(expression, true);
        //}
        //private static (string where, ParameterCollection? parms, Expression? expression) ParseExpression(Expression? expression, bool isroot)
        //{
        //    try
        //    {
        //        Expression? nullExpression = null;
        //        var res = (where: "", parms: new ParameterCollection(), nullExpression);
        //        if (expression == null)
        //            return res;
        //        if (isroot)
        //            res.where = "WHERE ";

        //        if (expression.NodeType == ExpressionType.OrElse || expression.NodeType == ExpressionType.AndAlso)
        //        {
        //            var exp = (BinaryExpression)expression;
        //            var left = ParseExpression(exp.Left, false);
        //            var right = ParseExpression(exp.Right, false);
        //            if (left.expression != null || right.expression != null)
        //                throw new NotImplementedException();
        //            if(left.parms != null)
        //                res.parms.AddRange(left.parms);
        //            if(right.parms != null)
        //                res.parms.AddRange(right.parms);
        //            if (exp.NodeType == ExpressionType.OrElse)
        //                res.where += $"({left.where}) OR ({right.where})";
        //            else
        //                res.where += $"(({left.where}) AND ({right.where}))";
        //        }
        //        else if (expression.NodeType == ExpressionType.Call)
        //        {
        //            var exp = (MethodCallExpression)expression;
        //            if (exp.Method.Name == "Contains")
        //            {
        //                var r = exp.GetType();
        //                if (typeof(MemberExpression).IsAssignableFrom(exp.Object!.GetType()))
        //                {
        //                    var member = ((MemberExpression)exp.Object).Member;
        //                    if (!member.DeclaringType!.IsSubclassOf(typeof(CoreObject)))
        //                        throw new Exception("Member Type is not of type TFM.Core.Base");
        //                    var prop = Nucleus.ObjectSchema.GetFromType(member.DeclaringType).AllProperties.SingleOrDefault(x => x.Name == member.Name);
        //                    if (prop != null && prop.IsDBColumn)
        //                    {
        //                        string[]? searcharray;
        //                        var arg = exp.Arguments[0];
        //                        if (arg != null && arg.NodeType == ExpressionType.MemberAccess)
        //                            searcharray = ((MemberExpression)arg).GetValue().ToString()?.Split((Char)32);
        //                        else if (arg != null && arg.NodeType == ExpressionType.Constant)
        //                            searcharray = ((ConstantExpression)arg).Value?.ToString()?.Split((Char)32);
        //                        else
        //                            throw new Exception("Unsupported argument in Contains method");
        //                        if(searcharray == null)
        //                            return res;
        //                        var qry = "";
        //                        for (int i = 0; i < searcharray.Length; i++)
        //                        {
        //                            var param = new Data.Parameter(member.Name, searcharray[i], prop.HasFullTextIndex ? Operator.Contains : Operator.Like);
        //                            res.parms.Add(param);
        //                            qry += $"{param.SQLWhereCondition}";
        //                            if (i < searcharray.Length - 1)
        //                                qry += " AND ";
        //                        }
        //                        res.where += qry;
        //                        return res;
        //                    }
        //                    throw new Exception("Property is missing the DBColumn Attribute");
        //                }
        //                else if (typeof(MethodCallExpression).IsAssignableFrom(exp.GetType()))
        //                {
        //                    return (where: "", parms: new ParameterCollection(), exp);
        //                }


        //            }
        //            else if (exp.Method.Name == "ContainsAll")
        //            {
        //                var member = ((MemberExpression)exp.Arguments[0]).Member;
        //                if (!member.DeclaringType?.IsSubclassOf(typeof(CoreObject)) ?? true)
        //                    throw new Exception("Member Type is not of type TFM.Core.Base");
        //                var prop = Nucleus.ObjectSchema.GetFromType(member.DeclaringType ?? throw new NullReferenceException()).AllProperties.SingleOrDefault(x => x.Name == member.Name);
        //                if (prop != null && prop.IsDBColumn)
        //                {
        //                    string[] searcharray;
        //                    StringComparison? comparison;
        //                    var arg1 = exp.Arguments[1];
        //                    if (arg1.NodeType == ExpressionType.MemberAccess)
        //                        comparison = (StringComparison)((MemberExpression)arg1).GetValue();
        //                    else if (arg1.NodeType == ExpressionType.Constant)
        //                        comparison = (StringComparison)(((ConstantExpression)arg1).Value ?? throw new NullReferenceException("Argument value is null"));
        //                    else
        //                        throw new Exception("Unsupported argument in ContainsAll method");
        //                    var arg2 = exp.Arguments[2];
        //                    if (arg2.NodeType == ExpressionType.MemberAccess)
        //                        searcharray = (string[])((MemberExpression)arg2).GetValue();
        //                    else if (arg2.NodeType == ExpressionType.Constant)
        //                        searcharray = (string[])(((ConstantExpression)arg2).Value ?? throw new NullReferenceException("Argument value is null"));
        //                    else
        //                        throw new Exception("Unsupported argument in ContainsAll method");
        //                    var ignorecase = false;
        //                    switch (comparison)
        //                    {
        //                        case StringComparison.CurrentCultureIgnoreCase:
        //                            ignorecase = true;
        //                            break;
        //                        case StringComparison.InvariantCultureIgnoreCase:
        //                            ignorecase = true;
        //                            break;
        //                        case StringComparison.OrdinalIgnoreCase:
        //                            ignorecase = true;
        //                            break;
        //                    }
        //                    var qry = "";

        //                    for (int i = 0; i < searcharray.Length; i++)
        //                    {

        //                        var param = new Data.Parameter(member.Name, searcharray[i],
        //                            prop.HasFullTextIndex ? (ignorecase ? Operator.Contains : Operator.ContainsCaseSensitive) :
        //                            (ignorecase ? Data.Operator.Like : Data.Operator.LikeCaseSensitive));
        //                        res.parms.Add(param);
        //                        qry += $"{param.SQLWhereCondition}";
        //                        if (i < searcharray.Length - 1)
        //                            qry += " AND ";
        //                    }
        //                    res.where += qry;
        //                    return res;

        //                }
        //                throw new Exception("Property is missing the DBColumn Attribute");
        //            }
        //            else if (exp.Method.Name == "Where")
        //            {

        //            }
        //        }
        //        else if (expression.NodeType == ExpressionType.Not)
        //        {
        //            if (expression.Type == typeof(bool))
        //            {
        //                var e = (UnaryExpression)expression;
        //                if (e.Operand.Type == typeof(bool))
        //                {
        //                    throw new NotImplementedException();
        //                    //var param = new Parameter(((MemberExpression)e.Operand).Member.Name, false, Operator.Equals);
        //                    //res.where += param.SQLWhereCondition;
        //                    //res.parms.Add(param);
        //                }
        //                else
        //                    throw new Exception("Expression type(Not) not supported ");
        //            }
        //            else
        //                throw new Exception("Expression type not supported");
        //        }
        //        else if (expression.NodeType == ExpressionType.MemberAccess)
        //        {
        //            if (expression.Type == typeof(bool))
        //            {
        //                throw new NotImplementedException();
        //                //var param = new Parameter(((MemberExpression)expression).Member.Name, true, Operator.Equals);
        //                //res.where += param.SQLWhereCondition;
        //                //res.parms.Add(param);
        //            }
        //            else
        //                throw new Exception("Expression type not supported");
        //        }
        //        else if (expression.NodeType == ExpressionType.Quote)
        //        {

        //            if (expression.Type == typeof(bool))
        //            {
        //                throw new NotImplementedException();
        //                //var param = new Parameter(((MemberExpression)expression).Member.Name, true, Operator.Equals);
        //                //res.where += param.SQLWhereCondition;
        //                //res.parms.Add(param);
        //            }
        //            else
        //                throw new Exception("Expression type not supported");
        //        }
        //        else if (expression.NodeType == ExpressionType.Invoke)
        //        {
        //            var exp = ((InvocationExpression)expression).Expression;
        //            if (exp.NodeType == ExpressionType.Lambda)
        //            {
        //                //var lambda = (LambdaExpression)exp;
        //                var lambda = (LambdaExpression?)Evaluator.PartialEval(exp);
        //                var tempResult = ParseExpression(lambda?.Body, false);
        //                res.where += $"({tempResult.where})";
        //                res.parms.AddRange(tempResult.parms!);
        //                //var predicateExp = Expression.Lambda<Func<, bool>>(lambdaExpression.Body, lambdaExpression.Parameters)
        //            }
        //            else
        //                throw new Exception("Unknown Invokation Expression Type");




        //            //var tempResult = ParseExpression(exp, false);
        //            //res.where += tempResult.where;
        //            //res.parms.AddRange(tempResult.parms);




        //            //var left = ParseExpression(exp.Left, false);
        //            //var right = ParseExpression(exp.Right, false);
        //            //res.parms.AddRange(left.parms);
        //            //res.parms.AddRange(right.parms);
        //            //if (exp.NodeType == ExpressionType.OrElse)
        //            //    res.where += $"({left.where}) OR ({right.where})";
        //            //else
        //            //    res.where += $"({left.where} AND {right.where})";
        //        }
        //        else
        //        {

        //            var SqlOptr = Operator.Equals;
        //            switch (expression.NodeType)
        //            {
        //                case ExpressionType.Equal:
        //                    break;
        //                case ExpressionType.GreaterThan:
        //                    SqlOptr = Operator.GreaterThan;
        //                    break;
        //                case ExpressionType.GreaterThanOrEqual:
        //                    SqlOptr = Operator.GreaterThanOrEqualTo;
        //                    break;
        //                case ExpressionType.IsFalse:
        //                    throw new Exception("Expression type is not supported");
        //                case ExpressionType.IsTrue:
        //                    throw new Exception("Expression type is not supported");
        //                case ExpressionType.LessThan:
        //                    SqlOptr = Operator.LessThan;
        //                    break;
        //                case ExpressionType.LessThanOrEqual:
        //                    SqlOptr = Operator.LessThanOrEqualTo;
        //                    break;
        //                case ExpressionType.NotEqual:
        //                    SqlOptr = Operator.NotEquals;
        //                    break;

        //                default:
        //                    throw new Exception("Expression type on the left is not supported");
        //            }
        //            var exp = (BinaryExpression)expression;
        //            var left = "";
        //            bool isTFMObject = false;
        //            if (exp.Left.NodeType == ExpressionType.MemberAccess)
        //            {
        //                var member = ((MemberExpression)exp.Left).Member;
        //                if (member.GetUnderlyingType().IsSubclassOf(typeof(CoreObject)))
        //                {
        //                    if (Nucleus.ObjectSchema.GetFromType(member.DeclaringType ?? throw new NullReferenceException("Member declaring type was null")).AllDBColumns.SingleOrDefault(x => x.Name == $"{member.Name}ID") == null)
        //                        throw new Exception($"Expression declaring type {member.DeclaringType.FullName} does not contain the sql column '{member.Name}ID'. You will need to select correct column directly.");
        //                    isTFMObject = true;
        //                    left = $"{member.Name}ID";
        //                }
        //                else
        //                    left = member.Name;
        //            }
        //            else if (exp.Left.NodeType == ExpressionType.Convert)
        //                left = ((MemberExpression)((UnaryExpression)exp.Left).Operand).Member.Name;
        //            else
        //                throw new Exception($"Expression type on the left is not supported. {exp.Left.NodeType.ToString()}");

        //            object? o = null;
        //            switch (exp.Right.NodeType)
        //            {
        //                case ExpressionType.Call:
        //                    if (!isTFMObject)
        //                        o = ((MethodCallExpression)exp.Right).GetValue();
        //                    else
        //                    {
        //                        var tfm = (CoreObject)((MethodCallExpression)exp.Right).GetValue();
        //                        o = tfm != null ? tfm.ID : Guid.Empty;
        //                    }
        //                    break;
        //                case ExpressionType.MemberAccess:
        //                    o = ((MemberExpression)exp.Right).GetValue();
        //                    break;
        //                case ExpressionType.Constant:
        //                    if (!isTFMObject)
        //                        o = ((ConstantExpression)exp.Right).Value;
        //                    else
        //                    {
        //                        var tfm = (CoreObject?)((ConstantExpression)exp.Right).Value;
        //                        o = tfm != null ? tfm.ID : Guid.Empty;
        //                    }
        //                    break;
        //                default:
        //                    throw new Exception($"Expression type on the right is not supported. {exp.Right.NodeType.ToString()} ");
        //            }
        //            throw new NotImplementedException();
        //            //var param = new Parameter(left, o, SqlOptr);
        //            //res.where += param.SQLWhereCondition;
        //            //res.parms.Add(param);
        //        }
        //        return res;
        //    }
        //    catch (Exception ex)
        //    {
        //        if (isroot)
        //        {
        //            var msg = $"Error parsing expression tree into SQL. {ex.Message}";
        //            throw new Exception(msg, ex);
        //        }
        //        else
        //        {
        //            var msg = $"{ex.Message} {expression?.ToString()}";
        //            throw new Exception(msg, ex);
        //        }
        //    }
        //}
        
        
        
        //protected void DataRowToClass(DataRow dr)
        //{
        //    Type classType = this.GetType();
        //    List<string> columnNames = dr.Table.Columns.Cast<DataColumn>().Select(column => column.ColumnName).ToList();
        //    try
        //    {
        //        this.IsLoading = true;
        //        foreach (var prop in _ObjectSchema.AllDBColumns)
        //        {
        //            if (prop.CanWrite)   // Make sure property isn't read only
        //            {
        //                if (dr[prop.Name] != System.DBNull.Value)   // Don't copy over DBNull
        //                {

        //                    object propertyValue = dr[prop.Name];
        //                    try
        //                    {
        //                        propertyValue = Convert.ChangeType(
        //                                dr[prop.Name],
        //                                prop.Type);
        //                    }
        //                    catch
        //                    {
        //                        var con = TypeDescriptor.GetConverter(prop.Type);
        //                        if (con.CanConvertFrom(propertyValue.GetType()))
        //                            propertyValue = con.ConvertFrom(dr[prop.Name]);
        //                    }
        //                    prop.BackingField?.SetValue(this, propertyValue);
        //                }

        //            }
        //        }
        //        this.IsLoading = false;
        //        this.Changes.Clear();

        //    }
        //    catch (Exception ex)
        //    {
        //        throw new Exception($"Unable to convert the DataRow into a {_ObjectSchema} object.", ex);
        //    }
        //}
        private static Dictionary<string, object?>[] CreateDictionaryFromDT<T>(DataTable dt)
        {
            return CreateDictionaryFromDT(dt, typeof(T));
            //var acc = TypeAccessor.Create(typeof(T));
            //IList<PropertyInfo> propertyList = typeof(T).GetProperties();
            //List<string> columnNames = dt.Columns.Cast<DataColumn>().Select(column => column.ColumnName).ToList();
            //List<string> defs = new List<string>();
            //List<Dictionary<string, object>> values = new List<Dictionary<string, object>>();
            //try
            //{
            //    foreach (PropertyInfo property in propertyList)
            //        if (property != null && property.CanWrite)   // Make sure property isn't read only
            //            if (columnNames.Contains(property.Name))  // If property is a column name
            //                defs.Add(property.Name);
            //    foreach (DataRow dr in dt.Rows)
            //    {
            //        Dictionary<string, object> dict = new Dictionary<string, object>();
            //        foreach (string s in defs)
            //        {
            //            object o = dr[s];
            //            if (o.Equals(DBNull.Value))
            //                dict.Add(s, null);
            //            else
            //                dict.Add(s, o);
            //        }
            //        values.Add(dict);
            //    }
            //}
            //catch (Exception ex)
            //{
            //    throw new Exception("Unable to convert the " + typeof(T).Name + " data structure into a Dictionary collection.", ex);
            //}
            //return values;
        }
        private static Dictionary<string, object?>[] CreateDictionaryFromDT(DataTable dt, Type type)
        {
            //todo - can be optimised further using multiple threads
            //var properties = ObjectSchema.Get(type).AllDBColumns;
            //List<string> columnNames = dt.Columns.Cast<DataColumn>().Select(column => column.ColumnName).ToList();
            var rowCount = dt.Rows.Count;
            //List<string> defs = new List<string>();
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
        //private static List<T> ExtractCollection<T>(Dictionary<string, object>[] val) 
        //{
        //    return ExtractCollection(val, typeof(T)).ChangeType<T>();
        //}

        //private static List<T> ExtractObjects<T>(Dictionary<string, object>[] sqlData) 
        //{
        //    var objs = BuildObjectCollection<T>(sqlData);
        //    return objs;
        //}
        //private static List<object> ExtractObjects<T>(Dictionary<string, object>[] sqlData, Type type)
        //{
        //    var objs = BuildObjectCollection<T>(sqlData);
        //    //if (ObjectSchema.GetObjectSchema<T>().HasEnumerableChildProperties)
        //    //{
        //    //    foreach (var child in ObjectSchema.GetObjectSchema<T>().EnumerableChildProperties)
        //    //    {
        //    //        ExtractChildObjects(objs, child.BoundPropertyInfo, child.ObjectSchema);
        //    //    }
        //    //}
        //    return objs;
        //}
        //private static void ExtractChildObjects(List<object> parentCollection, ObjectSchema childSchema) 
        //{
        //    try
        //    {
        //        var parentIDs = parentCollection.Select(x => (x as Base).ID).ToArray();
        //        var childObjs = ExtractCollection(LoadChildObjectsFromDB(parentIDs, childSchema), childSchema.Type);
        //        BindChildObjects(parentCollection, childObjs, childSchema.Type);
        //    }
        //    catch (Exception ex)
        //    {
        //        throw new Exception($"Unable to extract and bind child objects of type -'{childSchema.Type}' for parent of type -'{typeof(T)}'. {ex.Message}", ex);
        //    }
        //}







        private static void BindChildObjects(List<object> parentCollection, List<object> childCollection)
        {
            try
            {
                var parentType = parentCollection.First().GetType();
                var childType = childCollection.First().GetType();
                var parentSchema = Nucleus.ObjectSchema.Get(parentType);
                var childSchema = Nucleus.ObjectSchema.Get(childType);

                Type[] methodArgs = { typeof(List<object>), typeof(List<object>) };
                DynamicMethod Bind = new DynamicMethod("Bind", null, methodArgs, true);
                ILGenerator il = Bind.GetILGenerator();
                LocalBuilder loc_parentCollection = il.DeclareLocal(typeof(Dictionary<Guid, object>));
                LocalBuilder loc_childCollection = il.DeclareLocal(typeof(List<object>));
                LocalBuilder loc_childObject = il.DeclareLocal(childType);
                LocalBuilder loc_parentObject = il.DeclareLocal(parentType);
                LocalBuilder loc_index = il.DeclareLocal(typeof(int));



                ////CREATE indexed dictionary collection of parent objects
                LocalBuilder loc_parentArray = il.DeclareLocal(parentType.MakeArrayType());
                LocalBuilder loc_parentID = il.DeclareLocal(typeof(Guid));
                Label enterloop = il.DefineLabel();
                Label loopagain = il.DefineLabel();
                il.Emit(OpCodes.Newobj, typeof(Dictionary<Guid, object>).GetConstructors()[0]);
                il.Emit(OpCodes.Stloc, loc_parentCollection);

                //create array and store
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, typeof(Enumerable).GetMethod("ToArray", BindingFlags.Static | BindingFlags.Public)!.MakeGenericMethod(typeof(object)));
                il.Emit(OpCodes.Stloc, loc_parentArray);

                //create index value 0 and store
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Stloc, loc_index);
                //begin loop
                il.Emit(OpCodes.Br, enterloop);
                il.MarkLabel(loopagain);
                //loop through parent objects and add to dictionary based on id field as key
                //load new iteration from parentArray and store
                il.Emit(OpCodes.Ldloc, loc_parentArray);
                il.Emit(OpCodes.Ldloc, loc_index);
                il.Emit(OpCodes.Ldelem_Ref);
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Stloc, loc_parentObject);
                il.Emit(OpCodes.Callvirt, parentSchema!.GetProperty("ID").Getter!);
                il.Emit(OpCodes.Stloc, loc_parentID);

                //get grouped collection, Parent Guid, child object and add to group
                il.Emit(OpCodes.Ldloc, loc_parentCollection);
                il.Emit(OpCodes.Ldloc, loc_parentID);
                il.Emit(OpCodes.Ldloc, loc_parentObject);
                il.Emit(OpCodes.Callvirt, typeof(Dictionary<Guid, object?>).GetMethod("Add")!);

                //add 1 to the index
                il.Emit(OpCodes.Ldloc, loc_index);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Stloc, loc_index);

                //entry point for second loop
                il.MarkLabel(enterloop);

                il.Emit(OpCodes.Ldloc, loc_index);
                il.Emit(OpCodes.Ldloc, loc_parentArray);
                il.Emit(OpCodes.Ldlen);
                // if there are still items in the list loop again    
                il.Emit(OpCodes.Blt, loopagain);


                ////CREATE grouped collection of child objects
                LocalBuilder loc_objGroup = il.DeclareLocal(typeof(ObjectCollection<>).MakeGenericType(childType));
                LocalBuilder loc_childArray = il.DeclareLocal(childType.MakeArrayType());
                Label enterloop2 = il.DefineLabel();
                Label loopagain2 = il.DefineLabel();
                il.Emit(OpCodes.Newobj, typeof(ObjectCollection<>).MakeGenericType(childType).GetConstructors()[0]);
                il.Emit(OpCodes.Stloc, loc_objGroup);

                //create array and store
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Call, typeof(Enumerable).GetMethod("ToArray", BindingFlags.Static | BindingFlags.Public)!.MakeGenericMethod(typeof(object)));
                il.Emit(OpCodes.Stloc, loc_childArray);

                //create index value 0 and store
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Stloc, loc_index);
                //begin loop
                il.Emit(OpCodes.Br, enterloop2);
                il.MarkLabel(loopagain2);
                //loop through child objects and add to grouping based on parent id field
                //load new iteration from childArray and store
                il.Emit(OpCodes.Ldloc, loc_childArray);
                il.Emit(OpCodes.Ldloc, loc_index);
                il.Emit(OpCodes.Ldelem_Ref);
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Stloc, loc_childObject);
                il.Emit(OpCodes.Callvirt, Nucleus.ObjectSchema.Get(childType).ParentProperty!.Getter!);
                il.Emit(OpCodes.Stloc, loc_parentID);


                //set  parentobject on child
                il.Emit(OpCodes.Ldloc, loc_childObject);
                il.Emit(OpCodes.Ldloc, loc_parentCollection);
                //get parentid and load onto stack
                il.Emit(OpCodes.Ldloc, loc_parentID);
                //get parentobject from dictionary
                il.Emit(OpCodes.Callvirt, typeof(Dictionary<Guid, object?>).GetMethod("get_Item")!);
                //bind parentobject to child
                il.Emit(OpCodes.Callvirt, Nucleus.ObjectSchema.Get(childType).ParentProperty!.Setter!);


                //get grouped collection, Parent Guid, child object and add to group
                il.Emit(OpCodes.Ldloc, loc_objGroup);
                il.Emit(OpCodes.Ldloc, loc_parentID);
                il.Emit(OpCodes.Ldloc, loc_childObject);
                il.Emit(OpCodes.Callvirt, (typeof(ObjectCollection<>).MakeGenericType(childType)).GetMethod("Add")!);

                //add 1 to the index
                il.Emit(OpCodes.Ldloc, loc_index);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Stloc, loc_index);

                //entry point for second loop
                il.MarkLabel(enterloop2);

                il.Emit(OpCodes.Ldloc, loc_index);
                il.Emit(OpCodes.Ldloc, loc_childArray);
                il.Emit(OpCodes.Ldlen);
                // if there are still items in the list loop again    
                il.Emit(OpCodes.Blt, loopagain2);





                ////BIND ChildObjectCollection to Parent
                Label enterloop3 = il.DefineLabel();
                Label loopagain3 = il.DefineLabel();


                //create index value 0 and store
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Stloc, loc_index);
                //begin loop
                il.Emit(OpCodes.Br, enterloop3);
                il.MarkLabel(loopagain3);
                //loop through parent objects and bind child object collection
                //load new iteration from parentArray and store
                il.Emit(OpCodes.Ldloc, loc_parentArray);
                il.Emit(OpCodes.Ldloc, loc_index);
                il.Emit(OpCodes.Ldelem_Ref);
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Stloc, loc_parentObject);
                il.Emit(OpCodes.Callvirt, Nucleus.ObjectSchema.Get(parentType).GetProperty("ID").Getter!);
                il.Emit(OpCodes.Stloc, loc_parentID);

                //load child collection from dictionary and set value
                il.Emit(OpCodes.Ldloc, loc_parentObject);
                //il.Emit(OpCodes.Callvirt, ObjectSchema.Get(parentType).GetChildCollectionProperty(childType).GetGetMethod()); roll back if needed
                il.Emit(OpCodes.Callvirt, Nucleus.ObjectSchema.Get(parentType).TryGetChildCollectionProperty(typeof(ChildCollection<,>).MakeGenericType(childType, parentType))!.PropertyInfo.GetGetMethod()!);
                il.Emit(OpCodes.Ldloc, loc_objGroup);
                il.Emit(OpCodes.Ldloc, loc_parentID);
                il.Emit(OpCodes.Callvirt, typeof(ObjectCollection<>).MakeGenericType(childType).GetMethod("GetValue")!);
                il.Emit(OpCodes.Callvirt, typeof(ChildCollection<,>).MakeGenericType(childType, parentType).GetMethod("AddRange")!);

                //add 1 to the index
                il.Emit(OpCodes.Ldloc, loc_index);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Stloc, loc_index);

                //entry point for third loop
                il.MarkLabel(enterloop3);

                il.Emit(OpCodes.Ldloc, loc_index);
                il.Emit(OpCodes.Ldloc, loc_parentArray);
                il.Emit(OpCodes.Ldlen);
                // if there are still items in the list loop again    
                il.Emit(OpCodes.Blt, loopagain3);


                il.Emit(OpCodes.Ret);
                //Invoke and return
                object[] prms = { parentCollection, childCollection };
                Bind.Invoke(null, prms);
                return;
            }
            catch (TargetInvocationException tix)
            {
                throw new Exception("Error thrown trying to bind child objects.", tix.InnerException);
            }

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
                //il.Emit(OpCodes.Dup);
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
                //il.Emit(OpCodes.Callvirt, typeof(Dictionary<string, object>[]).GetMethod("get_Item"));
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
                    if (typeof(TFMInitialiseWithString).IsAssignableFrom(propType))
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
                    //var setter = ObjectSchema.GetObjectSchema(type).dProperties.GetValueOrDefault(kvp.Key).GetSetMethod();
                    //if (setter == null)
                    //    setter = ObjectSchema.GetObjectSchema(type).dProperties.GetValueOrDefault(kvp.Key).GetSetMethod(true);
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
                //var getcount = typeof(Dictionary<string, object>[]).GetMethod("get_Count");
                //il.Emit(OpCodes.Callvirt, getcount);
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
                //il.Emit(OpCodes.Dup);
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
                //il.Emit(OpCodes.Callvirt, typeof(Dictionary<string, object>[]).GetMethod("get_Item"));
                il.Emit(OpCodes.Stloc, loc_dictionary);
                var setters = new Dictionary<string, MethodInfo>();
                foreach (KeyValuePair<string, object> k in sqlData[0])
                {
                    //var setter = objectSchema.GetProperty(k.Key).Setter ObjectSchema.GetFromType(type).dProperties.GetValueOrDefault(k.Key).GetSetMethod();
                    //if (setter == null)
                    //    setter = ObjectSchema.GetFromType(type).dProperties.GetValueOrDefault(k.Key).GetSetMethod(true);
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
                    if (typeof(TFMInitialiseWithString).IsAssignableFrom(propType))
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
                    //var setter = ObjectSchema.GetObjectSchema(type).dProperties.GetValueOrDefault(kvp.Key).GetSetMethod();
                    //if (setter == null)
                    //    setter = ObjectSchema.GetObjectSchema(type).dProperties.GetValueOrDefault(kvp.Key).GetSetMethod(true);
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
                //var getcount = typeof(Dictionary<string, object>[]).GetMethod("get_Count");
                //il.Emit(OpCodes.Callvirt, getcount);
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
                //il.Emit(OpCodes.Dup);
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
                //il.Emit(OpCodes.Callvirt, typeof(Dictionary<string, object>[]).GetMethod("get_Item"));
                il.Emit(OpCodes.Stloc, loc_dictionary);
                var setters = new Dictionary<string, MethodInfo>();
                foreach (KeyValuePair<string, object> k in val[0])
                {
                    //var setter = ObjectSchema.GetFromType(type).dProperties.GetValueOrDefault(k.Key).GetSetMethod();
                    //if (setter == null)
                    //    setter = ObjectSchema.GetFromType(type).dProperties.GetValueOrDefault(k.Key).GetSetMethod(true);
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
                    if (typeof(TFMInitialiseWithString).IsAssignableFrom(propType))
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
                    //var setter = ObjectSchema.GetObjectSchema(type).dProperties.GetValueOrDefault(kvp.Key).GetSetMethod();
                    //if (setter == null)
                    //    setter = ObjectSchema.GetObjectSchema(type).dProperties.GetValueOrDefault(kvp.Key).GetSetMethod(true);
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
                //var getcount = typeof(Dictionary<string, object>[]).GetMethod("get_Count");
                //il.Emit(OpCodes.Callvirt, getcount);
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
                    //using (var d = new Context(data_Context))
                    //{
                    //    DataTable dt = d.GetData($"SELECT {property} FROM {SqlSchema.TableName}", new Parameter("ID", ID));
                    //    if (dt.Rows.Count > 0)
                    //    {
                    //        var o = dt.Rows[0][0];
                    //        if (o == DBNull.Value)
                    //        {
                    //            output = default(T);
                    //            return false;
                    //        }
                    //        else
                    //        {
                    //            output = (T)o;
                    //            return true;
                    //        }
                    //    }
                    //}
                }
                catch (Exception) { }
            }
            output = default;
            return false;
        }
        [JsonIgnore]
        public bool HasChanges
        {
            get => Changes.HasChanges;
            //get
            //{
            //    if (objectSchema.HasEnumerableChildProperties)
            //        foreach (var childCollection in objectSchema.EnumerableChildProperties)
            //            foreach (var childObj in (IEnumerable)childCollection.BoundPropertyInfo.GetValue(this))
            //                (childObj as ITFMHasChanges).ClearChanges();
            //    return Changes.HasChanges;
            //}
        }
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


        //string ITFMHasChanges.MemberName { get => _memberName; set => _memberName = value; }

        //private ObjectSchema _objectSchema;
        //private ObjectSchema SetObjectSchema()
        //{
        //    _objectSchema = ObjectSchema.Get(this);
        //    return _objectSchema;
        //}






        protected void UpdateProperty<T>(T value, [CallerMemberName] string property = null)
        {
            var prop = ObjectSchema.AllProperties.SingleOrDefault(x => x.Name == property);
            var implementsHasChanges = typeof(ITFMHasChanges).IsAssignableFrom(typeof(T));
            //if (implementsHasChanges)
            //((ITFMHasChanges)value).MemberName = property;
            //var backingField = _objectSchema.dFields.GetValueOrDefault($"_{property}");
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
    public abstract class CoreObject<T> :  CoreObject, ICoreObject<T>
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
