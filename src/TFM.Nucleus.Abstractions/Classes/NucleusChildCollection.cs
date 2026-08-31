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
        private readonly List<TChild> _removedChildren = [];
        private ICoreObject CastParent => _parent as ICoreObject ?? throw new InvalidCastException("Error casting Parent object as ICoreObject");

        /// <summary>
        /// Children removed from the collection since the last save; consumed by the delete
        /// pipeline so removals persist. Cleared via <see cref="AcknowledgeRemovals"/> after commit.
        /// </summary>
        protected internal IReadOnlyList<TChild> RemovedChildren => _removedChildren;
        protected internal void AcknowledgeRemovals() => _removedChildren.Clear();


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

        /// <summary>Binds the child to the parent, hooks change notification and inserts it. Returns false for null.</summary>
        private bool BindChild(TChild obj, int index)
        {
            if (obj == null)
                return false;
            obj.BindParent(_parent);
            _inner.Insert(index, obj);
            ((ICoreObject)obj).PropertyChanged += Obj_PropertyChanged;
            return true;
        }

        /// <summary>Unhooks change notification and records the child for the pending-removal set.</summary>
        private void UnbindChild(TChild obj)
        {
            if (obj == null)
                return;
            ((ICoreObject)obj).PropertyChanged -= Obj_PropertyChanged;
            _removedChildren.Add(obj);
        }

        public TChild this[int index]
        {
            get => _inner[index];
            set
            {
                var previous = _inner[index];
                UnbindChild(previous);
                _inner.RemoveAt(index);
                if (!BindChild(value, index))
                    throw new ArgumentNullException(nameof(value));
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Replace, value, previous, index));
            }
        }

        public int Count => _inner.Count;
        public bool IsReadOnly => false;
        public bool Contains(TChild item) => _inner.Contains(item);
        public void CopyTo(TChild[] array, int arrayIndex) => _inner.CopyTo(array, arrayIndex);
        public int IndexOf(TChild item) => _inner.IndexOf(item);

        public void Insert(int index, TChild item)
        {
            if (BindChild(item, index))
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item, index));
        }

        public void RemoveAt(int index)
        {
            var item = _inner[index];
            UnbindChild(item);
            _inner.RemoveAt(index);
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, item, index));
        }

        public void Clear()
        {
            if (_inner.Count == 0)
                return;
            foreach (var item in _inner)
                UnbindChild(item);
            _inner.Clear();
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        public IEnumerator<TChild> GetEnumerator() => _inner.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        // Non-generic IList explicit implementation
        bool IList.IsFixedSize => false;
        bool IList.IsReadOnly => false;
        bool ICollection.IsSynchronized => false;
        object ICollection.SyncRoot => ((ICollection)_inner).SyncRoot;

        object? IList.this[int index]
        {
            get => _inner[index];
            set => this[index] = (TChild)value!;  // routes through your generic indexer
        }

        int IList.Add(object? value)
        {
            Add((TChild)value!);  // routes through your generic Add → AddAndBind
            return Count - 1;
        }

        void IList.Insert(int index, object? value) => Insert(index, (TChild)value!);
        void IList.Remove(object? value) => Remove((TChild)value!);
        bool IList.Contains(object? value) => Contains((TChild)value!);
        int IList.IndexOf(object? value) => IndexOf((TChild)value!);

        void ICollection.CopyTo(Array array, int index) => ((ICollection)_inner).CopyTo(array, index);



        public IEnumerable<ICoreObject> ChildObjects => this.Cast<ICoreObject>();

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
            if (item == null)
                return false;
            var index = _inner.IndexOf(item);
            if (index < 0)
                return false;
            UnbindChild(item);
            _inner.RemoveAt(index);
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, item, index));
            return true;
        }

        public IQuery? GetInsertUpdateQuery()
        {
            var provider = _parent.DataProvider
                ?? throw new InvalidOperationException(
                    $"{typeof(TParent).Name} has no data provider, so a child collection query cannot be built.");
            return provider.CreateInsertAndUpdateQuery(this);
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
