using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus
{
    public class ObjectSchemaProvider : IObjectSchemaProvider
    {
        public IObjectSchema? Get<T>() => ObjectSchema<T>.Get(); 

        public IObjectSchema? Get(Type type) => ObjectSchema.Get(type);

        public bool TryGet<T>(out IObjectSchema? schema) => ObjectSchema.TryGet<T>(out schema);

        public bool TryGet(Type type, out IObjectSchema? schema) => ObjectSchema.TryGet(type, out schema);

        public IEnumerable<IObjectSchema> GetAll() => ObjectSchema.GetAll();

    }
}
