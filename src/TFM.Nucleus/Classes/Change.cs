using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus
{
    public class Change
    {
        public string Property;
        public string? OldValue;
        public string? NewValue;
        public bool Suppress;
        public bool Abbreviate;
        public Change(string property, object oldvalue, object newvalue, bool supress, bool abbreviate)
        {
            Property = property;
            SetOldValue(oldvalue);
            SetNewValue(newvalue);
            Suppress = supress;
            Abbreviate = abbreviate;
        }
        internal void SetOldValue(object oldvalue)
        {
            if (oldvalue != null && oldvalue.GetType() != typeof(byte[])) { OldValue = oldvalue.ToString(); }
        }
        internal void SetNewValue(object newvalue)
        {
            if (newvalue != null && newvalue.GetType() != typeof(byte[]))
            { NewValue = newvalue.ToString(); }
        }
    }

}
