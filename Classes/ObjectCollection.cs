using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus
{
    public class ObjectCollection<T>
    {
        private Dictionary<Guid, List<T>> Coll { get; set; } = [];
        public ObjectCollection()
        { }
        public Dictionary<Guid, List<T>> GetKeyValuePairs { get => Coll; }
        public void Add(Guid g, T obj)
        {
            if (Coll.TryGetValue(g, out var l))
                l.Add(obj);
            else
                Coll.Add(g, new List<T>() { obj });
        }
        public List<T> GetValue(Guid id)
        {
            if (Coll.TryGetValue(id, out var l))
                return l;
            return [];
        }
    }
}
