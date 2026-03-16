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
    public class ChildCollection<TChild, TParent> : ObservableCollection<TChild>, IEnumerable<TChild>, IDisposable, IChildCollection
    where TChild : ICoreObject
    where TParent : ICoreObject
    {
        private TParent? _parent;
        public ChildCollection(TParent parent)
        {
            _parent = parent;
            CollectionChanged += ChildCollectionChanged;
        }

        public void AddRange(IEnumerable<TChild> range)
        {
            if (range != null)
                foreach (var obj in range)
                    this.Add(obj);
        }
        public new void Add(TChild obj)
        {
            if (_parent != null)
            {
                var s = obj as ICoreObject;
                s?.BindParent(_parent);
                //ObjectSchema<TChild>.Get().ParentProperty.SetValue(obj, _parent);
                //ObjectSchema<TChild>.Get().ParentIDProperty.SetValue(obj, _parent.ID );
            }
            base.Add(obj);
            obj.PropertyChanged += Obj_PropertyChanged;
        }

        private void Obj_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            //throw new NotImplementedException();
            //if (_parent != null)
            //    if (_parent.ObjectSchema.TryGetValue(this.GetType(), out var val))
            //       _parent.ChildPropertyChanged(this, new PropertyChangedEventArgs(val.Name));
            if (_parent != null)
            {
                //todo investigate ways to get the exact property name from the parent
                var props = _parent.ObjectSchema.TryGetChildCollectionProperty(typeof(TChild));// _parent.GetObjectSchema().TryGetChildCollectionProperty(this.GetType());
                _parent.ChildPropertyChanged(this, new PropertyChangedEventArgs(props?.Name));
            }
        }
        private void ChildCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_parent != null)
            {
                //todo investigate ways to get the exact property name from the parent
                var props = _parent.ObjectSchema.TryGetChildCollectionProperty(typeof(TChild));// _parent.GetObjectSchema().TryGetChildCollectionProperty(this.GetType());
                _parent.ChildPropertyChanged(this, new PropertyChangedEventArgs(props?.Name));
            }
        }

        public new void Remove(TChild obj)
        {
            obj.PropertyChanged -= Obj_PropertyChanged;
            base.Remove(obj);
        }

        public void Dispose()
        {
            CollectionChanged -= ChildCollectionChanged;
            _parent = default;
            ClearItems();
        }

        // IEnumerator<ICoreObject> IEnumerable<ICoreObject>.GetEnumerator() => (IEnumerator<ICoreObject>)(ICoreObject)GetEnumerator();
    }

    public interface IChildCollection
    {
    }
}
