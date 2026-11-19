using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using System.Threading.Tasks;
using TFM.Nucleus.Abstractions.Interfaces;
using TFM.Nucleus.Data;

namespace TFM.Nucleus
{
    public class NucleusChildCollection<TChild, TParent>
        : IList<TChild>, IList, IReadOnlyList<TChild>, INucleusChildCollection<TChild, ICoreObject<TParent>>
        where TChild : ICoreChildObject<TChild, TParent>, new()
        where TParent : ICoreObject<TParent>, new()
    {
        private readonly TParent _parent;
        private readonly List<TChild> _inner = [];
        // Every child taken out since the last save, new ones included so that adding one back
        // is recognised; only the stored ones are deleted.
        private readonly List<TChild> _removedChildren = [];
        private bool _isLoaded = true;
        private bool _isLazy;
        private Action<INucleusChildCollection>? _lazyLoader;
        private readonly object _loadLock = new();
        private ICoreObject CastParent => _parent as ICoreObject ?? throw new InvalidCastException("Error casting Parent object as ICoreObject");

        /// <summary>
        /// False when the parent was loaded without this collection's rows: they exist in storage
        /// but not here. A collection excluded from the load throws when it is read or removed
        /// from until <see cref="LoadAsync"/> runs. A lazy ([RetrieveOnDemand]) collection loads
        /// itself the first time it is read instead. Adding works either way, and the added
        /// children save with the parent.
        /// </summary>
        public bool IsLoaded => _isLoaded;

        /// <summary>
        /// Loads the collection when it was excluded, merging any children added since; does
        /// nothing when it is already loaded. Goes through the ambient runtime.
        /// </summary>
        public Task LoadAsync(CancellationToken cancellationToken = default) =>
            _isLoaded ? Task.CompletedTask : NucleusRuntime.Current.LoadChildCollectionAsync(this, cancellationToken);

        /// <summary>
        /// Makes sure the collection's stored children are here: a lazy collection loads them,
        /// once, however many threads read it at the same time; an excluded one throws.
        /// </summary>
        private void EnsureLoaded([CallerMemberName] string? member = null)
        {
            if (_isLoaded)
                return;
            if (_isLazy)
            {
                lock (_loadLock)
                {
                    if (!_isLoaded)
                    {
                        // The loader of the stack that loaded the parent; the ambient runtime
                        // only when the collection was marked lazy without one.
                        if (_lazyLoader is { } load)
                            load(this);
                        else
                            NucleusRuntime.Current.LoadChildCollection(this);
                    }
                }
                if (_isLoaded)
                    return;
            }
            throw new InvalidOperationException(
                $"The {typeof(TChild).Name} children of this {typeof(TParent).Name} were excluded when it was loaded, so {member} " +
                "cannot answer. Call LoadAsync() on the collection first, or load the parent without excluding it.");
        }

        void INucleusChildCollection.MarkUnloaded() => ((INucleusChildCollection)this).MarkUnloaded(lazy: false);

        void INucleusChildCollection.MarkUnloaded(bool lazy)
        {
            lock (_loadLock)
            {
                _isLoaded = false;
                _isLazy = lazy;
                _lazyLoader = null;
            }
        }

        void INucleusChildCollection.MarkLazy(Action<INucleusChildCollection> load)
        {
            ArgumentNullException.ThrowIfNull(load);
            lock (_loadLock)
            {
                _isLoaded = false;
                _isLazy = true;
                _lazyLoader = load;
            }
        }

        ICoreObject INucleusChildCollection.ParentObject => CastParent;

        /// <summary>
        /// The stored children removed since the last save. Saving the parent deletes their rows
        /// in the same transaction as its other writes; a removed child that was never saved is
        /// simply dropped. Cleared by <see cref="AcknowledgeRemovals"/> once the save commits.
        /// </summary>
        protected internal IReadOnlyList<TChild> RemovedChildren =>
            [.. _removedChildren.Where(c => !((ICoreObject)c).IsNew)];
        protected internal void AcknowledgeRemovals() => _removedChildren.Clear();

        IEnumerable<ICoreObject> INucleusChildCollection.RemovedObjects => RemovedChildren.Cast<ICoreObject>();
        void INucleusChildCollection.AcknowledgeRemovals() => AcknowledgeRemovals();

        /// <summary>
        /// True when removing a stored child and saving the parent deletes its row. Declared by
        /// [Deletable] on the child type, on the collection property, or on the parent or an owner
        /// above it; without one, removing a stored child throws.
        /// </summary>
        public bool DeletesRemovedChildren =>
            CastParent.ObjectSchema.TryGetChildCollectionProperty(typeof(TChild))?.DeletesRemovedChildren ?? false;


        IObjectSchema INucleusChildCollection.ObjectSchema => ObjectSchema;
        protected internal IObjectSchema ObjectSchema => _parent.ObjectSchema.GetObjectSchema<TChild>();

        [field: NonSerialized]
        public event NotifyCollectionChangedEventHandler? CollectionChanged;
        [field: NonSerialized]
        protected event PropertyChangedEventHandler? PropertyChanged;

        public NucleusChildCollection(TParent parent)
        {
            if(typeof(TParent).IsAssignableTo(typeof(ICoreObject)) == false)
                throw new ArgumentException($"Type {typeof(TParent).FullName} does not implement ICoreObject");
            if(typeof(TChild).IsAssignableTo(typeof(ICoreObject)) == false)
                throw new ArgumentException($"Type {typeof(TChild).FullName} does not implement ICoreObject");
            //var ss = new ObservableCollection<TChild>();
            _parent = parent;
            CollectionChanged += ChildCollectionChanged;
        }

        event NotifyCollectionChangedEventHandler? INotifyCollectionChanged.CollectionChanged
        {
            add => CollectionChanged += value;

            remove => CollectionChanged -= value;
        }

        event PropertyChangedEventHandler? INotifyPropertyChanged.PropertyChanged
        {
            add => PropertyChanged += value;

            remove => PropertyChanged -= value;
        }

        public void AddRange(IEnumerable<TChild> range)
        {
            if (range == null)
                return;
            var added = new List<TChild>();
            foreach (var obj in range)
                if (BindChild(obj, _inner.Count))
                    added.Add(obj);
            if (added.Count > 0)
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, added));
        }

        public void Add(TChild obj)
        {
            if (BindChild(obj, _inner.Count))
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, obj));
        }

        /// <summary>
        /// Binds the child to the parent, hooks change notification and inserts it. Returns false
        /// for null. A child removed from this collection since the last save comes back as it
        /// was, still bound to this parent, and is no longer pending deletion.
        /// </summary>
        private bool BindChild(TChild obj, int index)
        {
            if (obj == null)
                return false;
            var removed = _removedChildren.FindIndex(c => ReferenceEquals(c, obj));
            if (removed >= 0)
                _removedChildren.RemoveAt(removed);
            else
                obj.BindParent(_parent);
            _inner.Insert(index, obj);
            ((ICoreObject)obj).PropertyChanged += Obj_PropertyChanged;
            return true;
        }

        /// <summary>
        /// Throws, before anything changes, when a child about to leave the collection is stored
        /// and removals from this collection are not declared to delete. Removing it silently
        /// would leave its row behind with nothing pointing at the mistake.
        /// </summary>
        private void EnsureRemovable(TChild obj)
        {
            if (obj == null || ((ICoreObject)obj).IsNew || DeletesRemovedChildren)
                return;
            var property = CastParent.ObjectSchema.TryGetChildCollectionProperty(typeof(TChild))?.Name ?? "the collection";
            throw new InvalidOperationException(
                $"Removing a stored {typeof(TChild).Name} from {typeof(TParent).Name}.{property} would leave its row behind, because deleting " +
                $"{typeof(TChild).Name} rows through their owner is not enabled. Declare [Deletable] on {typeof(TChild).Name}, on " +
                $"{typeof(TParent).Name}.{property}, or on {typeof(TParent).Name} (or an owner above it) to have the save delete it.");
        }

        /// <summary>Unhooks change notification and records the child as removed.</summary>
        private void UnbindChild(TChild obj)
        {
            if (obj == null)
                return;
            ((ICoreObject)obj).PropertyChanged -= Obj_PropertyChanged;
            _removedChildren.Add(obj);
        }

        public TChild this[int index]
        {
            get
            {
                EnsureLoaded();
                return _inner[index];
            }
            set
            {
                EnsureLoaded();
                if (value == null)
                    throw new ArgumentNullException(nameof(value));
                var previous = _inner[index];
                if (ReferenceEquals(previous, value))
                    return;
                EnsureRemovable(previous);
                UnbindChild(previous);
                _inner.RemoveAt(index);
                BindChild(value, index);
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Replace, value, previous, index));
            }
        }

        public int Count
        {
            get
            {
                EnsureLoaded();
                return _inner.Count;
            }
        }
        public bool IsReadOnly => false;
        public bool Contains(TChild item)
        {
            EnsureLoaded();
            return _inner.Contains(item);
        }
        public void CopyTo(TChild[] array, int arrayIndex)
        {
            EnsureLoaded();
            _inner.CopyTo(array, arrayIndex);
        }
        public int IndexOf(TChild item)
        {
            EnsureLoaded();
            return _inner.IndexOf(item);
        }

        public void Insert(int index, TChild item)
        {
            EnsureLoaded();
            if (BindChild(item, index))
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item, index));
        }

        public void RemoveAt(int index)
        {
            EnsureLoaded();
            var item = _inner[index];
            EnsureRemovable(item);
            UnbindChild(item);
            _inner.RemoveAt(index);
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, item, index));
        }

        public void Clear()
        {
            EnsureLoaded();
            if (_inner.Count == 0)
                return;
            // All or nothing: every child is checked before any leaves.
            foreach (var item in _inner)
                EnsureRemovable(item);
            foreach (var item in _inner)
                UnbindChild(item);
            _inner.Clear();
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        public IEnumerator<TChild> GetEnumerator()
        {
            EnsureLoaded();
            return _inner.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        // Non-generic IList explicit implementation
        bool IList.IsFixedSize => false;
        bool IList.IsReadOnly => false;
        bool ICollection.IsSynchronized => false;
        object ICollection.SyncRoot => ((ICollection)_inner).SyncRoot;

        object? IList.this[int index]
        {
            get => this[index];
            set => this[index] = (TChild)value!;  // routes through your generic indexer
        }

        int IList.Add(object? value)
        {
            Add((TChild)value!);  // routes through your generic Add → AddAndBind
            return _inner.Count - 1;
        }

        void IList.Insert(int index, object? value) => Insert(index, (TChild)value!);
        void IList.Remove(object? value) => Remove((TChild)value!);
        bool IList.Contains(object? value) => Contains((TChild)value!);
        int IList.IndexOf(object? value) => IndexOf((TChild)value!);

        void ICollection.CopyTo(Array array, int index)
        {
            EnsureLoaded();
            ((ICollection)_inner).CopyTo(array, index);
        }



        /// <summary>
        /// The children held here, for saving, committing and deleting. For a collection that
        /// was excluded at load these are only the children added since, which is exactly what
        /// a save must write, so this never throws.
        /// </summary>
        public IEnumerable<ICoreObject> ChildObjects => _inner.Cast<ICoreObject>();

        void INucleusChildCollection.AttachLoaded(IEnumerable<ICoreObject> children)
        {
            ArgumentNullException.ThrowIfNull(children);
            lock (_loadLock)
            {
                var present = _inner.Select(c => ((ICoreObject)c).ID).ToHashSet();
                foreach (var child in children)
                {
                    if (child is not TChild typed)
                        throw new ArgumentException($"{child.GetType().Name} is not a {typeof(TChild).Name}.", nameof(children));
                    if (!present.Add(child.ID))
                        continue;
                    typed.BindLoadedParent(_parent);
                    _inner.Add(typed);
                    child.PropertyChanged += Obj_PropertyChanged;
                }
                _isLoaded = true;
                _isLazy = false;
                _lazyLoader = null;
            }
        }

        private void OnCollectionChanged(NotifyCollectionChangedEventArgs e) => CollectionChanged?.Invoke(this, e);

        private void Obj_PropertyChanged(object? sender, PropertyChangedEventArgs e) => NotifyParent();

        private void ChildCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => NotifyParent();

        /// <summary>
        /// Raises PropertyChanged on the parent for the property exposing this collection, and on
        /// this collection for anything subscribed through INotifyPropertyChanged. The collection
        /// exposes that interface, so failing to raise it left every such subscriber silent.
        /// </summary>
        private void NotifyParent()
        {
            if (_parent == null)
                return;
            var collectionProperty = CastParent.ObjectSchema.TryGetChildCollectionProperty(typeof(TChild));
            var args = new PropertyChangedEventArgs(collectionProperty?.Name);
            CastParent.ChildPropertyChanged(this, args);
            PropertyChanged?.Invoke(this, args);
        }

        public bool Remove(TChild item)
        {
            EnsureLoaded();
            if (item == null)
                return false;
            var index = _inner.IndexOf(item);
            if (index < 0)
                return false;
            EnsureRemovable(_inner[index]);
            UnbindChild(_inner[index]);
            _inner.RemoveAt(index);
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, item, index));
            return true;
        }

        public IQuery? GetInsertUpdateQuery()
        {
            var provider = _parent.DataProvider
                ?? throw new InvalidOperationException(
                    $"{typeof(TParent).Name} has no data provider, so a child collection query cannot be built.");
            return provider.CreateInsertAndUpdateQuery(_inner);
        }

        public Type ChildType => typeof(TChild);




        #region IDisposable Support
        private bool disposedValue;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    foreach (var item in _inner)
                        if (item is ICoreObject coreObj)
                            coreObj.PropertyChanged -= Obj_PropertyChanged;
                    _inner.Clear();
                    _removedChildren.Clear();
                    CollectionChanged = null;
                    PropertyChanged = null;
                }
                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
