using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus
{
    public class NucleusCollection<T> : List<T>, INucleusCollection<T> where T : ICoreObject<T>, new()
    {
        [field: NonSerialized]
        public event NotifyCollectionChangedEventHandler? CollectionChanged;
        [field: NonSerialized]
        protected event PropertyChangedEventHandler? PropertyChanged;

        public NucleusCollection() 
        {
            if (typeof(T).IsAssignableTo(typeof(ICoreObject)) == false)
                throw new ArgumentException($"Type {typeof(T).FullName} does not implement ICoreObject");
        }
        public NucleusCollection(IEnumerable<T> objs) : this()
        {
            AddAndBindRange(objs, false);
        }

        public new void Add(T obj)
        {
            if (obj == null)
                return;
            base.Add(obj);
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, obj, Count - 1));
        }

        public new void AddRange(IEnumerable<T> objs) => AddAndBindRange(objs, true);

        private void AddAndBindRange(IEnumerable<T> objs, bool fireChanged)
        {
            var added = new List<T>();
            foreach (var obj in objs)
                if (obj != null)
                {
                    base.Add(obj);
                    added.Add(obj);
                }
            if (fireChanged && added.Count > 0)
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, added));
        }

        public new void Insert(int index, T obj)
        {
            base.Insert(index, obj);
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, obj, index));
        }

        public new bool Remove(T obj)
        {
            var index = IndexOf(obj);
            if (index < 0)
                return false;
            base.RemoveAt(index);
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, obj, index));
            return true;
        }

        public new void RemoveAt(int index)
        {
            var obj = this[index];
            base.RemoveAt(index);
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, obj, index));
        }

        public new void Clear()
        {
            if (Count == 0)
                return;
            base.Clear();
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        private void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
        {
            CollectionChanged?.Invoke(this, e);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        }

        internal static NucleusCollection<T> BuildCollection(DataSet dataSet)
        {
            if (dataSet.RowCount == 0)
                return [];
            if (dataSet.DataTableSchema == null)
                throw new InvalidOperationException("DataSet does not have a DataTableSchema associated with it.");
            return [.. dataSet.BindRows<T>()];
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




        #region IDisposable Support
        private bool disposedValue;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    base.Clear();
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
