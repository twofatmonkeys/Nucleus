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
        // The values the server returned for this object's columns during a save that has not
        // been committed in memory yet. They become stored values when the object's changes are
        // committed, so a save that fails leaves the object as it was. Null until one arrives.
        private Dictionary<string, object?>? _serverValues;
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
        /// When the row was last written. The server stamps the stored value on every write. A
        /// save writes the object saved and only the rows below it that are new or changed, so a
        /// child's stamp moves when the child changes, not on every save of its parent. The
        /// stamped value is not read back, because reading it back slowed batched and bulk saves
        /// by 10% to 65% (see docs/code-review-findings.md). In memory, each row a save wrote
        /// takes the client clock when the save is committed, so it is close to the stored value
        /// but not equal to it. Mark a column of your own ReturnOutputToProperty to have a server
        /// value read back.
        /// </summary>
        [DatabaseColumnAttribute(Indexed = true, IndexDescending = true,
            SetValueOverride = ServerFunctionKind.CurrentDateTimeOffset)]
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



        private void CommitChanges(bool makeReadOnly, ILogger? logger = null) => CommitChanges(makeReadOnly, true, logger, null);
        /// <summary>
        /// Makes pending changes committed once a save has written them. A root also commits the
        /// forceSave links the save wrote (read from the same <see cref="SaveGraph"/> the save
        /// was built from, before anything here is committed); every object commits its held
        /// children and forgets the removals the save deleted. With an <paramref name="undo"/>
        /// log, each object records what it changes first, so a transaction that does not commit
        /// can put it back. <paramref name="stamp"/> is the boxed Timestamp of the commit, boxed
        /// once by the root and shared by every row below it the save wrote.
        /// </summary>
        private void CommitChanges(bool makeReadOnly, bool isRootObj, ILogger? logger, CommitUndoLog? undo, object? stamp = null)
        {
            // The rows the save wrote, as RelationalDataProvider.WriteQueries decides: the object
            // the save was asked for always, a dependent only when it has something of its own.
            // Read before anything is committed, which clears both.
            var written = isRootObj || SaveGraph.IsChanged(this);
            var graph = isRootObj && SaveGraph.CanReachDependents(this) ? SaveGraph.Walk([this]) : null;
            var links = graph?.Links ?? [];
            var members = graph?.Members ?? [];
            var record = undo?.GetOrAdd(this, () => new CommitUndo(this));
            record?.BeforeCommit();
            if (_serverValues is { Count: > 0 })
                record?.BeforeServerValues(_serverValues.Keys);
            LogChanges(logger);
            if (makeReadOnly)
                _isReadOnly = true;
            if (!IsNew)
            {
                foreach (var newProp in _newProperties)
                    _properties[newProp.Key] = newProp.Value;
                _newProperties.Clear();
            }
            // What the server stored wins over what the object sent.
            if (_serverValues is { Count: > 0 })
            {
                foreach (var (property, value) in _serverValues)
                {
                    _newProperties.Remove(property);
                    _properties[property] = value;
                }
                _serverValues.Clear();
            }
            // Timestamp is stamped by the server but not read back, so a row the save wrote takes
            // the local clock here. A row it did not write keeps the value it was loaded with.
            if (written)
                _properties[nameof(Timestamp)] = stamp ??= DateTimeOffset.Now;
            _isNew = false;
            if (ObjectSchema.HasNucleusChildCollections)
                foreach (var coll in NucleusChildCollections)
                {
                    record?.BeforeAcknowledge(coll, coll.RemovedObjects);
                    coll.AcknowledgeRemovals();
                    foreach (var child in coll.ChildObjects)
                    {
                        // Not as a root: the root's graph already holds the links below its children.
                        if (child is CoreObject coreChild)
                            coreChild.CommitChanges(false, false, null, undo, stamp);
                        else
                            CommitDependent(child, undo);
                    }
                }
            if (ObjectSchema.HasLinkedCollections)
                foreach (var collection in NucleusLinkedCollections)
                {
                    record?.BeforeAcknowledge(collection, collection.RemovedMembers);
                    collection.AcknowledgeRemovals();
                }
            foreach (var link in links)
            {
                if (link is CoreObject coreLink)
                    coreLink.CommitChanges(makeReadOnly, false, null, undo, stamp);
                else
                    CommitDependent(link, undo);
            }
            // Members are roots of their own, committed as links are: the root's graph holds
            // everything below them.
            foreach (var member in members)
            {
                if (member is CoreObject coreMember)
                    coreMember.CommitChanges(makeReadOnly, false, null, undo, stamp);
                else
                    CommitDependent(member, undo);
            }
        }

        /// <summary>Commits a dependent that is not a <see cref="CoreObject"/>, recording it when the commit is undoable.</summary>
        private static void CommitDependent(ICoreObject dependent, CommitUndoLog? undo)
        {
            if (undo == null)
                dependent.CommitChanges();
            else
                dependent.CommitChangesInTransaction(undo);
        }

        /// <summary>
        /// What the commits of one object inside a transaction changed, from before the first, so
        /// the object can be put back if the transaction does not commit. A property written by any
        /// of the commits gets its stored value back and its current value as a pending change,
        /// unless the two are equal; a property no commit wrote is left alone. The removals each
        /// commit forgot are put back in their collections.
        /// </summary>
        private sealed class CommitUndo(CoreObject owner) : ICommitUndo
        {
            private readonly bool _wasNew = owner._isNew;
            // The Timestamp from before the first commit, which every commit that wrote the row moved.
            private readonly (bool Had, object? Value) _timestamp =
                owner._properties.TryGetValue(nameof(Timestamp), out var stamp) ? (true, stamp) : (false, null);
            // The stored value of each property a commit wrote, from before the first commit;
            // HadValue false when the property held nothing. Only for an object that was not new:
            // one that was new goes back to holding every value as a change.
            private Dictionary<string, (bool HadValue, object? Value)>? _originals;
            // Each property a commit took the server's value for, as it was before the first such
            // commit: its stored value and its pending one, each with whether it had one. Put back
            // exactly, since the commit replaced both.
            private Dictionary<string, (bool HadStored, object? Stored, bool HadPending, object? Pending)>? _serverOriginals;
            private List<(object Collection, List<ICoreObject> Removed)>? _removals;

            /// <summary>Records the stored value of each pending property not recorded by an earlier commit.</summary>
            public void BeforeCommit()
            {
                if (_wasNew || owner._newProperties.Count == 0)
                    return;
                _originals ??= [];
                foreach (var key in owner._newProperties.Keys)
                    if (!_originals.ContainsKey(key))
                        _originals[key] = owner._properties.TryGetValue(key, out var value) ? (true, value) : (false, null);
            }

            /// <summary>Records each property about to take the server's value that an earlier commit did not.</summary>
            public void BeforeServerValues(IEnumerable<string> properties)
            {
                _serverOriginals ??= [];
                foreach (var key in properties)
                    if (!_serverOriginals.ContainsKey(key))
                    {
                        var hadStored = owner._properties.TryGetValue(key, out var stored);
                        var hadPending = owner._newProperties.TryGetValue(key, out var pending);
                        _serverOriginals[key] = (hadStored, stored, hadPending, pending);
                    }
            }

            /// <summary>
            /// Puts back each property a commit took the server's value for, stored and pending
            /// value both. An edit made since the commit, pending now because a committed object
            /// holds its edits as changes, is kept: on a new object <see cref="Revert"/> then makes
            /// it the value, and on a loaded one it stays pending.
            /// </summary>
            private void RevertServerValues()
            {
                if (_serverOriginals == null)
                    return;
                foreach (var (key, original) in _serverOriginals)
                {
                    var editedSince = owner._newProperties.TryGetValue(key, out var edit);
                    if (original.HadStored)
                        owner._properties[key] = original.Stored;
                    else
                        owner._properties.Remove(key);
                    if (editedSince)
                        owner._newProperties[key] = edit;
                    else if (original.HadPending)
                        owner._newProperties[key] = original.Pending;
                    else
                        owner._newProperties.Remove(key);
                }
            }

            /// <summary>Records the removals a collection is about to forget.</summary>
            public void BeforeAcknowledge(object collection, IEnumerable<ICoreObject> removed)
            {
                List<ICoreObject>? list = null;
                foreach (var obj in removed)
                    (list ??= []).Add(obj);
                if (list != null)
                    (_removals ??= []).Add((collection, list));
            }

            public void Revert()
            {
                // Server values first: a new object's edits since the commit, moved below, and a
                // loaded one's, kept here, still win over what the property held before.
                RevertServerValues();
                if (_wasNew)
                {
                    // The edits made since the commit are pending; a new object holds them all.
                    foreach (var pending in owner._newProperties)
                        owner._properties[pending.Key] = pending.Value;
                    owner._newProperties.Clear();
                    owner._isNew = true;
                }
                else if (_originals != null)
                {
                    foreach (var (key, original) in _originals)
                    {
                        if (_serverOriginals?.ContainsKey(key) == true)
                            continue;
                        var current = owner._newProperties.TryGetValue(key, out var pending)
                            ? pending
                            : owner._properties.GetValueOrDefault(key);
                        if (original.HadValue)
                            owner._properties[key] = original.Value;
                        else
                            owner._properties.Remove(key);
                        if (original.HadValue && Equals(current, original.Value))
                            owner._newProperties.Remove(key);
                        else
                            owner._newProperties[key] = current;
                    }
                }
                if (_timestamp.Had)
                    owner._properties[nameof(Timestamp)] = _timestamp.Value;
                else
                    owner._properties.Remove(nameof(Timestamp));
                owner._isValidated = false;
                if (_removals != null)
                    foreach (var (collection, removed) in _removals)
                    {
                        if (collection is INucleusChildCollection children)
                            children.RestoreRemovals(removed);
                        else if (collection is INucleusLinkedCollection linked)
                            linked.RestoreRemovals(removed);
                    }
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

        /// <summary>
        /// The linked collection behind a <c>NucleusLinkedCollection</c> property, created on first
        /// read. Reading it loads nothing: the collection loads its members the first time one is
        /// read.
        /// </summary>
        protected internal NucleusLinkedCollection<TMany, TOne> GetLinkedCollection<TMany, TOne>([CallerMemberName] string? property = null)
            where TOne : CoreObject, ICoreObject<TOne>, new()
            where TMany : ICoreObject<TMany>, new()
        {
            ArgumentNullException.ThrowIfNull(property);
            if (_properties.TryGetValue(property, out var value) && value is NucleusLinkedCollection<TMany, TOne> existing)
                return existing;
            lock (_properties)
            {
                if (_properties.TryGetValue(property, out value) && value is NucleusLinkedCollection<TMany, TOne> raced)
                    return raced;
                var collection = new NucleusLinkedCollection<TMany, TOne>((TOne)this, property);
                _properties[property] = collection;
                return collection;
            }
        }

        /// <summary>
        /// The object's linked collections that exist: one never read since the object was created
        /// or loaded holds nothing to save, so it is not created here.
        /// </summary>
        public IEnumerable<INucleusLinkedCollection> NucleusLinkedCollections
        {
            get
            {
                if (ObjectSchema.HasLinkedCollections)
                    foreach (var p in ObjectSchema.LinkedCollectionProperties)
                        if (_properties.TryGetValue(p.Name, out var value) && value is INucleusLinkedCollection collection)
                            yield return collection;
            }
        }

        ILinkedObjectLoader? ICoreObject.LinkedObjectLoader => _linkedObjectLoader;

        

        /// <summary>
        /// The current value of a property. A stored null (a NULL column, or a value cleared on
        /// write) reads as the type's default, the same as a property that was never set and the
        /// same as <c>RowQuery.Get</c>. Every column is nullable and an unset property is written
        /// as NULL, so a plain cast would unbox null into an int property and throw
        /// NullReferenceException on reload. Declare the property nullable (int?) to tell NULL
        /// from 0. A value of the wrong type still throws. A loaded <c>[LinkedObject]</c> holds
        /// only the referenced entity's ID until it is first read; that read loads the entity
        /// (<see cref="ResolveLinkedObject"/>).
        /// </summary>
        protected internal T? GetValue<T>([CallerMemberName] string? property = null)
        {
            ArgumentNullException.ThrowIfNull(property);
            if (!IsReadOnly && !IsNew && _newProperties.TryGetValue(property, out var newValue))
                return newValue is null ? default : (T)newValue;
            if (_properties.TryGetValue(property, out var value) && value is not null)
                return value is T typed ? typed : StoredValueAs<T>(property, value);
            return default;
        }

        /// <summary>
        /// A stored value that is not already a <typeparamref name="T"/>: the ID a load stored for
        /// a <c>[LinkedObject]</c>, or for the <c>[ParentObject]</c> of a child loaded on its own
        /// (by a link to it, <c>LoadByID</c> or a query of its type), resolved to its entity; or
        /// else the plain cast, which throws for a value of the wrong type as it always has. A
        /// child loaded with its parent holds the parent object itself and never gets here. Kept
        /// out of line so the common read stays one type test.
        /// </summary>
        private T? StoredValueAs<T>(string property, object value)
        {
            if (value is Guid id && ObjectSchema.TryGetProperty(property) is { } reference && (reference.IsLinkedObject || reference.IsParentObject))
                return (T?)ResolveLinkedObject(reference, id);
            return (T)value;
        }

        /// <summary>
        /// The stack that loaded this object, which its <c>[LinkedObject]</c> properties are read
        /// through. Null for an object Nucleus did not load, or loaded through a path that bound
        /// none; such a read goes through the ambient runtime.
        /// </summary>
        private ILinkedObjectLoader? _linkedObjectLoader;

        void ICoreObject.BindLinkedObjectLoader(ILinkedObjectLoader loader)
        {
            ArgumentNullException.ThrowIfNull(loader);
            _linkedObjectLoader = loader;
        }

        /// <summary>
        /// Loads the entity a <c>[LinkedObject]</c>, or a standalone child's <c>[ParentObject]</c>,
        /// refers to, once, and keeps it as the stored value, so the object is not made dirty and
        /// the next read runs no query. Several threads reading it at once load it once. A row
        /// that no longer exists reads as null, and is looked for again on the next read, since
        /// the ID is still what is stored. A parent loads as <c>LoadByID</c> loads it, so its child
        /// collection holds instances of its own, not this one.
        /// </summary>
        private ICoreObject? ResolveLinkedObject(IPropertySchema link, Guid id)
        {
            lock (_properties)
            {
                if (_properties.TryGetValue(link.Name, out var held) && held is ICoreObject resolved)
                    return resolved;
                var loader = _linkedObjectLoader
                    ?? NucleusRuntime.Current as ILinkedObjectLoader
                    ?? throw new InvalidOperationException(
                        $"{GetType().Name}.{link.Name} holds the ID of a {link.Type.Name}, and nothing is available to load it: " +
                        "the object was not loaded by Nucleus and the ambient runtime cannot load linked objects.");
                var entity = loader.LoadLinkedObject(link.Type, id);
                if (entity is not null)
                    _properties[link.Name] = entity;
                return entity;
            }
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

        /// <summary>
        /// Writes the audit entry for this commit. Nothing is built when there is no logger, the
        /// type is [SuppressLogging], or the logger would drop an Information entry: children
        /// commit with no logger, and building their text only to throw it away cost every
        /// commit an allocation per object. The test used to be <c>!SuppressLogging || logger
        /// != null</c>, so every manager save, which passes a logger, audited suppressed types.
        /// </summary>
        private void LogChanges(ILogger? logger = null)
        {
            if (logger is null || ObjectSchema.SuppressLogging || !logger.IsEnabled(LogLevel.Information))
                return;
            try
            {
                var sb = new StringBuilder(IsNew ? $"Created {this}" : $"Updated {this}");
                if (!IsNew)
                    sb.Append(_getChangeLogEntry());
                logger.LogInformation(sb.ToString());
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

        void ICoreObject.CommitChangesInTransaction(CommitUndoLog undo, ILogger? logger)
        {
            ArgumentNullException.ThrowIfNull(undo);
            CommitChanges(false, true, logger, undo);
        }

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
        /// Takes the value the server stored for a property during a write, coerced as a loaded
        /// value is. It is held until the object's changes are committed, which happens once the
        /// save's transaction commits (or, through a caller's context, as its save runs), and then
        /// becomes the stored value, replacing any value the object sent. Until then the property
        /// reads as it did, so a save that fails, and is rolled back, leaves the object as it was,
        /// ready to be saved again. The next save drops anything held from one that did not commit.
        /// </summary>
        public void AcceptServerValue(string property, object? value)
        {
            ArgumentNullException.ThrowIfNull(property);
            (_serverValues ??= [])[property] = value != null && value != DBNull.Value ? CoerceBoundValue(property, value) : null;
        }

        /// <summary>
        /// Drops the server values held from a save that did not commit, before a new save writes
        /// the object: its write returns them again, and one it does not write must not take them.
        /// </summary>
        private void DropHeldServerValues() => _serverValues?.Clear();

















        public string DisplayName { get { return this.ToString(); } }




        //Overwrite these properties in child classes to change behaviour
        [Obsolete("Has no effect. Suppress an entity's audit entries with [SuppressLogging] on the type.")]
        protected internal virtual bool DisableLogging => false;
        [Obsolete("Has no effect. A type is cached when it is marked [Cacheable], and only then.")]
        protected internal virtual bool AllowCaching => true;
        [Obsolete("Has no effect. Set the interval on the attribute: [Cacheable(refreshInterval, cacheLifetime)].")]
        protected internal virtual int CacheRefreshInterval => 120;
        [Obsolete("Has no effect. Set the lifetime on the attribute: [Cacheable(refreshInterval, cacheLifetime)].")]
        protected internal virtual int CacheLifetime => 900;

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





























        /// <summary>
        /// Runs once a load has finished this object: its columns and parent are bound, its loaded
        /// children are attached and have run theirs, and an excluded collection reads as unloaded.
        /// Every load path calls it, through <see cref="ICoreObject.OnAfterLoadFromDB"/>.
        /// </summary>
        protected internal virtual void OnAfterLoadFromDB()
        {

        }

        void ICoreObject.OnAfterLoadFromDB() => OnAfterLoadFromDB();
        [Obsolete("Never called: schema initialisation has no instance to call it on. Run start-up work after InitializeDataProviderAsync.")]
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
            if (!_validatingAsDependent)
            {
                if (ObjectSchema.HasParentObject)
                    throw new CoreObjectException(CoreObjectExceptionType.UnableToSaveFromChildObject);
                // A save validates the dependents itself, from the one walk it also runs their
                // save hooks from; a direct call to Validate() walks here.
                if (!_dependentsValidatedBySave && HasChangedLinkedObjects(out var objs))
                    ValidateDependents(objs!);
            }
        }

        /// <summary>Validates each dependent not yet validated, as a dependent.</summary>
        private static void ValidateDependents(CoreObject[] dependents)
        {
            foreach (var dependent in dependents)
                if (!dependent._isValidated)
                    dependent.LinkedObjectValidate();
        }

        /// <summary>
        /// True while a save runs this object's <see cref="Validate"/>: the save then validates the
        /// dependents itself, so the graph is walked once for their rules and their save hooks.
        /// </summary>
        private bool _dependentsValidatedBySave;

        /// <summary>
        /// True while this object validates as a dependent of the object being saved (a child or a
        /// forceSave link), which skips the root's checks and cascade. False, its default, means
        /// root: an object built without running its constructor or field initialisers (the
        /// schema's emit and reflection builders) used to start as not-root and skip them.
        /// </summary>
        private bool _validatingAsDependent;

        private void LinkedObjectValidate()
        {
            _validatingAsDependent = true;
            try
            {
                Validate();
            }
            finally
            {
                // A dependent whose rules throw must not stay a dependent: a forceSave link is
                // saved as a root later, and would skip its own cascade.
                _validatingAsDependent = false;
            }
        }
        /// <summary>
        /// Runs on every save, after <see cref="Validate"/>, on the object being saved and on each
        /// new or changed child and forceSave link the save writes, before anything is written. A
        /// value set here is written but not validated. Override to fill in values a write needs.
        /// </summary>
        public virtual void PreSave()
        {
        }

        /// <summary>
        /// Every save path calls this once for each object it saves: the object's rules (with its
        /// dependents'), the saveable checks, then the save hooks of the object and its dependents.
        /// </summary>
        void ICoreObject.PrepareForSave()
        {
            _dependentsValidatedBySave = true;
            try
            {
                Validate();
            }
            finally
            {
                _dependentsValidatedBySave = false;
            }
            // The one walk of the dependents, after the object's own rules as Validate() orders it.
            HasChangedLinkedObjects(out var dependents);
            if (dependents != null)
                ValidateDependents(dependents);
            EnsureSaveable();
            DropHeldServerValues();
            PreSave();
            if (dependents != null)
                foreach (var dependent in dependents)
                {
                    dependent.DropHeldServerValues();
                    dependent.PreSave();
                }
        }

        /// <summary>Saves the object through the ambient runtime, which validates it first.</summary>
        public virtual void Save() => NucleusRuntime.Current.Save(this);

        public virtual Task SaveAsync() => SaveAsync(CancellationToken.None);

        /// <inheritdoc cref="Save()"/>
        public virtual Task SaveAsync(CancellationToken token) => NucleusRuntime.Current.SaveAsync(this, token);

        /// <summary>
        /// Saves within a caller-owned context (and transaction); the context validates the object
        /// first, and the caller commits or rolls back. The context marks the object saved as its
        /// writes run, and puts it back as it was if the transaction rolls back or its commit fails.
        /// </summary>
        public virtual Task SaveAsync(IDataContext dataContext, CancellationToken token = default)
        {
            ArgumentNullException.ThrowIfNull(dataContext);
            return dataContext.SaveAsync(this, token);
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







        [Obsolete("Use Save. A batch too large for ordinary statements is written by the bulk engine automatically.")]
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public static void BulkSave<TObject>(IEnumerable<TObject> objs) where TObject : CoreObject, new() =>
            NucleusRuntime.Current.Save(objs);

        /// <summary>
        /// Saves objects of any types, each routed to the provider that stores it, in one
        /// transaction per provider. Nulls are skipped. A batch too large for ordinary statements
        /// is written by the provider's bulk engine on the asynchronous path.
        /// </summary>
        public static void Save(IEnumerable<ICoreObject?> objs) => NucleusRuntime.Current.Save(objs);

        /// <summary>The asynchronous form of the batch save, which lets the bulk engine take a large batch.</summary>
        public static Task SaveAsync(IEnumerable<ICoreObject?> objs, CancellationToken cancellationToken = default) =>
            NucleusRuntime.Current.SaveAsync(objs, cancellationToken);

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

        /// <summary>Loads by primary key on the asynchronous path, or null when there is no such row.</summary>
        public static Task<T?> LoadByIDAsync<T>(Guid id, CancellationToken cancellationToken = default) where T : CoreObject<T>, new() =>
            NucleusRuntime.Current.LoadByIDAsync<T>(id, null, cancellationToken);

        [Obsolete("Use LoadFirstWhere. This loads every matching row to return the first.")]
        public static T? LoadObjectWhere<T>(Expression<Func<T, bool>> expression) where T : CoreObject<T>, new() =>
            LoadWhere(expression).FirstOrDefault();

        /// <summary>The first row the predicate matches, or null. One row is read, however many match.</summary>
        public static T? LoadFirstWhere<T>(Expression<Func<T, bool>> predicate) where T : CoreObject<T>, new() =>
            NucleusRuntime.Current.LoadFirst(predicate);

        /// <inheritdoc cref="LoadFirstWhere{T}(Expression{Func{T, bool}})"/>
        public static Task<T?> LoadFirstWhereAsync<T>(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) where T : CoreObject<T>, new() =>
            NucleusRuntime.Current.LoadFirstAsync(predicate, null, cancellationToken);

        public static NucleusCollection<T> LoadAll<T>() where T : CoreObject<T>, new() =>
            NucleusRuntime.Current.LoadAll<T>();

        /// <summary>Loads every row on the asynchronous path. A [Cacheable] type answers from its cache.</summary>
        public static Task<NucleusCollection<T>> LoadAllAsync<T>(CancellationToken cancellationToken = default) where T : CoreObject<T>, new() =>
            NucleusRuntime.Current.LoadAllAsync<T>(cancellationToken);

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


    public class CoreObjectException : TFM.Nucleus.NucleusException
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
