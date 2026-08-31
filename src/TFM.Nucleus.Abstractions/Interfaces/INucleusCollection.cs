using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus
{
    public interface INucleusCollection<T> : IList<T>, IEnumerable<T>, INotifyCollectionChanged, INotifyPropertyChanged, IDisposable
    {
    }
}
