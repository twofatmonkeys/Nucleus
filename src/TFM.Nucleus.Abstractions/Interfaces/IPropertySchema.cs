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


        [Obsolete("Use IsFullTextSearch.")]
        public bool HasFullTextIndex { get; }
        public bool IsDBColumn { get; }
        public bool SkipOnDbRead { get; }
        public bool IsOverridden { get; }
        [Obsolete("Use IsIndexed.")]
        public bool IsNonClusteredIndex { get; }

        // The neutral names forward to the obsolete ones by default, so an implementation written
        // against the old names still compiles.
#pragma warning disable CS0618
        /// <summary>The column's text is indexed for full-text search where the provider supports it.</summary>
        public bool IsFullTextSearch => HasFullTextIndex;

        /// <summary>The column gets an index of its own.</summary>
        public bool IsIndexed => IsNonClusteredIndex;
#pragma warning restore CS0618
        public bool IsUnique { get; }
        public IObjectSchema? ChildSchema { get; }
        public IObjectSchema? ObjectSchema { get; }

        public object? GetValue(ICoreObject? obj);
        public object? GetDBValue(ICoreObject? obj);
        public object? GetDBValue(ICoreObject? obj, object? rawValue);

        public void SetValue(object? obj, object? value);
    }
}
