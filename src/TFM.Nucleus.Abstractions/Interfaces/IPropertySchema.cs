using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using TFM.Nucleus.Data;

namespace TFM.Nucleus
{
    public interface IPropertySchema
    {
        public PropertyInfo PropertyInfo { get; }
        public FieldInfo? BackingField { get; }
        public IDatabaseColumnAttribute? DatabaseColumnAttribute { get; }
        public MethodInfo? Getter { get; }
        public MethodInfo? Setter { get; }
        public object? Create(object? thisObj);
        //internal Func<object, object> privateGetter { get; }
        //internal Action<object, object> privateSetter { get; }
        //internal Func<object, object> rawGetter { get; }
        //internal Action<object, object> rawSetter { get; }

        public bool CanRead { get; }
        public bool CanWrite { get; }
        public bool RetrieveOnDemand { get; }
        public bool SuppressLogging { get; }
        public bool AbbreviatedLogging { get; }
        public bool LinkWhenLogging { get; }

        public bool IsLinkedObject { get; }
        public bool ForceLinkedSave { get; }
        public bool IsLinkedChildCollection { get; }
        public Type? ChildType { get; }
        public bool IsParentObject { get; }
        public string? ParentIDPropertyName { get; }
        public Type Type { get; }
        protected internal Type DBWriteType { get; }

        public string Name { get; }


        public bool HasFullTextIndex { get; }
        public bool IsDBColumn { get; }
        public bool SkipOnDbRead { get; }
        public bool IsOverridden { get; }
        public bool IsNonClusteredIndex { get; }
        public bool IsUnique { get; }
        public IObjectSchema? ChildSchema { get; }
        public IObjectSchema? ObjectSchema { get; }

        public object? GetValue(ICoreObject? obj);
        public object? GetDBValue(ICoreObject? obj);
        public object? GetDBValue(ICoreObject? obj, object? rawValue);

        public void SetValue(object? obj, object? value);
    }
}
