using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus
{
    [Obsolete("Used by nothing in Nucleus, and named as an interface though it is a static class. Call Activator.CreateInstance yourself.")]
    public static class IInitializeWithString<T> 
    {
        public static T? Initialize(string s)
        {
            return (T?)Activator.CreateInstance(typeof(T), s);
        }

        public static T? Initialize(string value, bool lazyLoad, bool hasChanges)
        {
            return (T?)Activator.CreateInstance(typeof(T), value, lazyLoad, hasChanges);
        }
    }
}
