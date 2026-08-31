using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TFM.Nucleus.Data;

namespace TFM.Nucleus.Abstractions.Interfaces
{
    public interface INucleusChildCollection
    {
        IQuery? GetInsertUpdateQuery();
        public IObjectSchema ObjectSchema { get; }

        public Type ChildType { get; }
        public IEnumerable<ICoreObject> ChildObjects { get; }
    }

    public interface INucleusChildCollection<TChild, TParent> : INucleusChildCollection,  IEnumerable<TChild>, INotifyCollectionChanged, INotifyPropertyChanged, IDisposable
    {
    }
}
