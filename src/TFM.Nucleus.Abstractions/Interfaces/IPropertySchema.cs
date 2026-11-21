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

        /// <summary>True when the property carries the obsolete [LinkedChildCollection], which has no effect.</summary>
        [Obsolete("[LinkedChildCollection] has no effect. A child collection is a NucleusChildCollection property; test ChildType.")]
        public bool IsLinkedChildCollection { get; }

        /// <summary>The child type of a NucleusChildCollection property; null for any other property.</summary>
        public Type? ChildType { get; }

        /// <summary>The [Deletable] declared on the property itself, if any. Only a child collection may carry one.</summary>
        public DeletableAttribute? DeleteDeclaration => null;

        /// <summary>
        /// For a child collection, true when removing a stored child and saving the parent
        /// deletes the child's row. Declared by [Deletable] on the child type, on this property,
        /// or on the parent or any owner above it. False for any other property.
        /// </summary>
        public bool DeletesRemovedChildren => false;

        /// <summary>
        /// For a child collection marked [RetrieveOnDemand]: a parent load leaves its rows out
        /// and the collection loads them the first time it is read.
        /// </summary>
        public bool IsLazyChildCollection => false;

        /// <summary>True for a <c>NucleusLinkedCollection</c> property.</summary>
        public bool IsLinkedCollection => false;

        /// <summary>The many type of a <c>NucleusLinkedCollection</c> property; null for any other property.</summary>
        public Type? LinkedType => null;

        /// <summary>
        /// For a <c>NucleusLinkedCollection</c> property, the many type's key property its
        /// <c>[LinkedBy]</c> names; null for any other property.
        /// </summary>
        public string? LinkedByPropertyName => null;
        public bool IsParentObject { get; }
        public string? ParentIDPropertyName { get; }
        public Type Type { get; }
        protected internal Type DBWriteType { get; }

        public string Name { get; }


        [Obsolete("Use IsFullTextSearch.")]
        public bool HasFullTextIndex { get; }
        public bool IsDBColumn { get; }
        [Obsolete("Set for a [ParentObject] property and read by nothing.")]
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
