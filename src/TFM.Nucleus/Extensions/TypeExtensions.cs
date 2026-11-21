using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus
{ 
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class TypeExtensions
    {
        public static bool IsCacheable(this Type t)
        {
            if (t.IsNucleus())
            {
                var sc = ObjectSchema.Get(t);
                return sc.IsCacheable;
            }
            return false;
        }
        public static bool IsNucleus(this Type t)
        {
            return t.IsSubclassOf(typeof(CoreObject));
        }

        public static bool IsCacheable<T>(this T _)
        {
            return typeof(T).IsCacheable();
        }
    }
}
