using TFM.Nucleus.Data;
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
        /// <summary>
        /// When the row was last written. The server stamps the stored value on every write; the
        /// value in memory is the client clock at the first commit and is not read back, because
        /// reading it back slowed batched and bulk saves by 10% to 65% (see
        /// docs/code-review-findings.md). Mark a column of your own ReturnOutputToProperty to have
        /// a server value read back.
        /// </summary>
        [DatabaseColumnAttribute(Indexed = true, IndexDescending = true,
            SetValueOverride = eServerFunction.CurrentDateTimeOffset)]
        public DateTimeOffset Timestamp
        {
            get { return GetValue<DateTimeOffset>(); }
        }


        IObjectSchema ICoreObject.ObjectSchema => ObjectSchema;

        // Cached per instance. The schema is read on every SetValue, ToString and CommitChanges,
        // and resolving it means a dictionary lookup keyed on GetType() each time. The field is
        // left null rather than initialised, so an object materialised without a constructor still
        // resolves it on first use.
        private IObjectSchema? _objectSchema;
        protected internal IObjectSchema ObjectSchema => _objectSchema ??= Nucleus.ObjectSchema.Get(GetType());

        Dictionary<string, object?> ICoreObject.ChangedProperties => ChangedProperties;
        protected internal Dictionary<string, object?> ChangedProperties => IsNew ? _properties : _newProperties;

        protected internal bool HasChangedProperties => IsReadOnly ? false : ChangedProperties.Count > 0;



        private void CommitChanges(bool makeReadOnly, ILogger? logger = null) => CommitChanges(makeReadOnly, true, logger);
        /// <summary>
        /// Makes pending changes committed once a save has written them. A root also commits the
        /// forceSave links the save wrote (read from the same <see cref="SaveGraph"/> the save
        /// was built from, before anything here is committed); every object commits its held
        /// children and forgets the removals the save deleted.
        /// </summary>
        private void CommitChanges(bool makeReadOnly, bool isRootObj, ILogger? logger = null)
        {
            var links = isRootObj && SaveGraph.CanReachDependents(this) ? SaveGraph.Walk([this]).Links : [];
            LogChanges(logger);
            if (makeReadOnly)
                _isReadOnly = true;
            if (!IsNew)
            {
                foreach (var newProp in _newProperties)
                    _properties[newProp.Key] = newProp.Value;
                _newProperties.Clear();
            }
            // Timestamp is stamped by the server but not read back, so a new object takes the
            // local clock here. A loaded object keeps the value it was loaded with.
            if (!_properties.ContainsKey(nameof(Timestamp)))
                _properties[nameof(Timestamp)] = DateTimeOffset.Now;
            _isNew = false;
            if (ObjectSchema.HasNucleusChildCollections)
                foreach (var coll in NucleusChildCollections)
                {
                    coll.AcknowledgeRemovals();
                    foreach (var child in coll.ChildObjects)
                    {
                        // Not as a root: the root's graph already holds the links below its children.
                        if (child is CoreObject coreChild)
                            coreChild.CommitChanges(false, false);
                        else
                            child.CommitChanges();
                    }
                }
            foreach (var link in links)
            {
                if (link is CoreObject coreLink)
                    coreLink.CommitChanges(makeReadOnly, false);
                else
                    link.CommitChanges();
            }
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
                    if (_properties.TryGetValue(property, out var current) && Equals(current, value))
                        return;
                    _properties[property] = value;
                }
                else
                {
                    // Already staged with this value: nothing to do.
                    if (_newProperties.TryGetValue(property, out var pending) && Equals(pending, value))
                        return;

                    if (_properties.TryGetValue(property, out var committed) && Equals(committed, value))
                    {
                        // Assigned back to the stored value, so the property is no longer dirty.
                        // Staging it anyway wrote a needless column on save and logged
                        // "updated X from A to A".
                        if (!_newProperties.Remove(property))
                            return;
                    }
                    else
                    {
                        _newProperties[property] = value;
                    }
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

        /// <summary>
        /// The child collection behind a <c>NucleusChildCollection</c> property, created on first
        /// read. A child entity can hold one too, so owned rows nest to any depth.
        /// </summary>
        protected internal NucleusChildCollection<TChild, TParent> GetChildCollection<TChild, TParent>([CallerMemberName] string? property = null)
            where TParent : CoreObject, ICoreObject<TParent>, new()
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
            _properties[property] = obj != null && obj != DBNull.Value ? CoerceBoundValue(property, obj) : null;

        }

        /// <summary>
        /// Widens a database integer into the enum its property is declared as. The reader returns
        /// the column's integral type, and while the CLR will unbox that straight into an enum it
        /// will not unbox it into a nullable one, so a WidgetKind? property would throw on its first
        /// read. Converting here keeps <see cref="GetValue{T}"/> a plain cast, and the guard keeps
        /// the schema lookup off every string, Guid and date column.
        /// </summary>
        private object CoerceBoundValue(string column, object value)
        {
            if (value is not (int or long or short or byte or sbyte or uint or ulong or ushort))
                return value;
            var propertyType = ObjectSchema.TryGetProperty(column)?.Type;
            var enumType = propertyType == null ? null : Nullable.GetUnderlyingType(propertyType) ?? propertyType;
            return enumType is { IsEnum: true } ? Enum.ToObject(enumType, value) : value;
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
                    // A key with no matching property is not a failure worth aborting the audit
                    // entry for: it happens when a column still exists in the database but the
                    // property has been removed from the class.
                    var schema = ObjectSchema.TryGetProperty(prop.Key);
                    if (schema is null)
                        continue;
                    if (!schema.SuppressLogging)
                    {
                        sb.Append(", ");
                        if (schema.AbbreviatedLogging)
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

        /// <summary>
        /// The objects a save of this one also writes and validates: the new or changed children
        /// in its child collections at every depth, and the forceSave links the save writes.
        /// </summary>
        protected internal bool HasChangedLinkedObjects(out CoreObject[]? objects)
        {
            if (!SaveGraph.CanReachDependents(this))
            {
                objects = null;
                return false;
            }
            objects = [.. SaveGraph.Walk([this]).ChangedDependents.OfType<CoreObject>()];
            return objects.Length > 0;
        }


        void ICoreObject.OnOutputParameterReceived(object? sender, IOutputReceivedEventArgs args) =>
        OnOutputParameterReceived(sender, args);
        protected internal void OnOutputParameterReceived(object? sender, IOutputReceivedEventArgs args)
        {
            AcceptServerValue(args.PropertyName!, args.Value);
            if (sender is not null and IQueryOutputParameter)
            {
                ((IQueryOutputParameter)sender).OutputReceived -= OnOutputParameterReceived;
            }
        }

        /// <summary>
        /// Takes the value the server stored for a property during a write. It is coerced as a
        /// loaded value is, becomes the committed value, and drops any pending client value, so
        /// committing the object's changes after the write keeps it.
        /// </summary>
        public void AcceptServerValue(string property, object? value)
        {
            _newProperties.Remove(property);
            _properties[property] = value != null && value != DBNull.Value ? CoerceBoundValue(property, value) : null;
        }

















        public string DisplayName { get { return this.ToString(); } }




        //Overwrite these properties in child classes to change behaviour
        protected internal virtual bool DisableLogging => false; //Setting true disables logging of actions within the Save() method of the object instance.
        protected internal virtual bool AllowCaching => true; //Setting true allows the object collection to be stored in the cache, false forces retrieval from the DB in every case
        [Obsolete("Has no effect. Deletion is declared with [Deletable] on the type, a child collection property or an owner.")]
        protected internal virtual bool AllowDeletions => false;
        protected internal virtual int CacheRefreshInterval => 120; //Cache refresh interval in seconds
        protected internal virtual int CacheLifetime => 900; //Cache lifetime interval in seconds

        /// <summary>True when <paramref name="t"/> is a concrete Nucleus entity type.</summary>
        public static bool IsDerivedClass(Type t)
        {
            ArgumentNullException.ThrowIfNull(t);
            // IsSubclassOf never matches an interface, so the original test was always false.
            return typeof(ICoreObject).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface;
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
        }





























        protected internal virtual void OnAfterLoadFromDB()
        {

        }
        protected internal virtual void OnAfterInitializeDB()
        {

        }

        /// <summary>
        /// Checks the object before a save; throw to reject it. On the object being saved, this
        /// also runs <c>Validate()</c> on every new or changed child in its child collections at
        /// every depth, and on each forceSave link the save writes, so a child's rules hold
        /// without the parent restating them. An override must call <c>base.Validate()</c>.
        /// </summary>
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
            await dataContext.SaveAsync(this, token);
            CommitChanges(false);
        }

        private void EnsureSaveable()
        {
            if (!_isValidated)
                throw new InvalidOperationException(
                    $"{this} has not been validated and cannot be saved. An override of Validate() must call " +
                    $"base.Validate() so that the object is marked valid.");
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

        /// <summary>
        /// Loads by primary key, leaving the selected child collections unloaded, as in
        /// <c>LoadByID&lt;Bundle&gt;(id, b =&gt; b.Files)</c>. Their rows are neither queried nor
        /// transferred; call <c>LoadAsync()</c> on a collection to fetch it later.
        /// </summary>
        public static T? LoadByID<T>(Guid id, params Expression<Func<T, INucleusChildCollection>>[] exclude) where T : CoreObject<T>, new() =>
            NucleusRuntime.Current.LoadByID<T>(id, ChildCollectionSelector.PropertyNames(exclude));

        /// <summary>Loads the matching rows, leaving the selected child collections unloaded.</summary>
        public static NucleusCollection<T> LoadWhere<T>(Expression<Func<T, bool>> expression, params Expression<Func<T, INucleusChildCollection>>[] exclude) where T : CoreObject<T>, new() =>
            NucleusRuntime.Current.LoadWhere<T>(expression.Body, ChildCollectionSelector.PropertyNames(exclude));

        /// <inheritdoc cref="LoadWhere{T}(Expression{Func{T, bool}}, Expression{Func{T, INucleusChildCollection}}[])"/>
        public static Task<NucleusCollection<T>> LoadWhereAsync<T>(Expression<Func<T, bool>> expression, CancellationToken token, params Expression<Func<T, INucleusChildCollection>>[] exclude) where T : CoreObject<T>, new() =>
            NucleusRuntime.Current.LoadWhereAsync<T>(expression.Body, ChildCollectionSelector.PropertyNames(exclude), token);

        #endregion

        #region Static delete API (forwarders over the ambient runtime)

        /// <summary>
        /// Deletes every matching row without loading any, with its child collection rows, in one
        /// transaction; the type must be [Deletable]. Returns the rows of <typeparamref name="T"/>
        /// affected. A predicate matching every row is refused.
        /// </summary>
        public static int DeleteWhere<T>(Expression<Func<T, bool>> predicate) where T : CoreObject<T>, new() =>
            NucleusRuntime.Current.DeleteWhere(predicate);

        /// <inheritdoc cref="DeleteWhere{T}(Expression{Func{T, bool}})"/>
        public static Task<int> DeleteWhereAsync<T>(Expression<Func<T, bool>> predicate, CancellationToken token = default) where T : CoreObject<T>, new() =>
            NucleusRuntime.Current.DeleteWhereAsync(predicate, token);

        #endregion

        [JsonIgnore]
        public bool HasChanges => HasChangedProperties;
        [JsonIgnore]
        protected internal bool IsLoading = false;

        /// <summary>Clears the recorded change summary on this object and its held children, loading none.</summary>
        public void ClearChanges()
        {
            foreach (var collection in NucleusChildCollections)
                foreach (var childObj in collection.ChildObjects)
                    (childObj as CoreObject)?.ClearChanges();

            _changes?.Clear();
        }

        private ChangeCollection? _changes;

        /// <summary>
        /// Legacy change summary collection, retained for compatibility. The v2 change tracking
        /// keeps committed and pending property dictionaries instead, so nothing populates this and
        /// it stays empty unless a derived type adds to it. Use <see cref="HasChanges"/> to ask
        /// whether an object is dirty, and ChangedProperties for what changed.
        /// </summary>
        /// <remarks>
        /// Created on demand rather than in the constructor: an object materialised from the
        /// database is built without running one, which previously left this null and made every
        /// call on it throw.
        /// </remarks>
        [JsonIgnore]
        public ChangeCollection Changes => _changes ??= [];

        /// <summary>
        /// True when the object has uncommitted property changes. Reports the same state as
        /// <see cref="HasChanges"/>; both previously read from different tracking systems and
        /// could disagree.
        /// </summary>
        [JsonIgnore]
        public virtual bool DataChanged => HasChangedProperties;








        internal void ChildPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!IsLoading)
                PropertyChanged?.Invoke(sender, e);
        }

    }

    [TypeConverter(typeof(NucleusTypeConverter))]
    public abstract class CoreObject<T> :  CoreObject, ICoreObject<T> where T : CoreObject<T>
    {
        /// <summary>
        /// Materialises entities from a <see cref="DataSet"/>. The rows come from the database, so
        /// the objects are bound as already persisted: binding through BindValue (rather than
        /// SetRawCurrentValue) clears IsNew once the ID arrives, without which every object
        /// returned here would be INSERTed again on save instead of updated.
        /// </summary>
        public static IEnumerable<T> BindDataSet(DataSet dataSet)
        {
            ArgumentNullException.ThrowIfNull(dataSet);
            if (dataSet.RowCount == 0)
                return [];
            var objList = new List<T>(dataSet.RowCount);
            for (int i = 0; i < dataSet.RowCount; i++)
            {
                // Construct normally: the constructor initialises the property dictionaries, and
                // BindValue then overwrites the seeded ID with the one from the row.
                var obj = Activator.CreateInstance<T>();
                var row = dataSet[i];
                for (int ii = 0; ii < dataSet.ColumnCount; ii++)
                    obj.BindValue(dataSet.ColumnNames[ii], row[ii]);
                obj.OnAfterLoadFromDB();
                objList.Add(obj);
            }
            return objList;
        }
    }

    public abstract class CoreChildObject<TParent> : CoreObject, ICoreObject, ICoreChildObject<TParent> where TParent : ICoreObject<TParent>
    {
        [ParentObject]
        [DatabaseColumn]
        public TParent Parent { get => GetValue<TParent>() ?? throw new InvalidOperationException("Parent object is not bound."); }

        void ICoreChildObject<TParent>.BindParent(TParent parent) => BindParent(parent);

        void ICoreChildObject<TParent>.BindLoadedParent(TParent parent)
        {
            ArgumentNullException.ThrowIfNull(parent);
            BindValue(nameof(Parent), parent);
        }
        protected internal void BindParent(TParent parent)
        {
            ArgumentNullException.ThrowIfNull(parent);
            if (GetRawCurrentValue(nameof(Parent)) is TParent)
                throw new CoreObjectException(CoreObjectExceptionType.ParentObjectAlreadyBound);
            SetValue(parent, property: nameof(Parent));
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
