using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus
{
    public interface ITFMHasChanges : INotifyPropertyChanged
    {
        public bool HasChanges { get; }
        public string MemberName { get; set; }
        public void ClearChanges();
    }
}
