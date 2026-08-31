using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus
{
    public interface IObjectSchemaProvider
    {
        public IObjectSchema? Get<T>();
        public IObjectSchema? Get(Type type);
        public bool TryGet<T>(out IObjectSchema? schema);
        public bool TryGet(Type type, out IObjectSchema? schema);
        public IEnumerable<IObjectSchema>? GetAll();

    }
}
