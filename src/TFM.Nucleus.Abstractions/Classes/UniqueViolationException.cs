using System;
using System.Collections.Generic;

namespace TFM.Nucleus.Data
{
    /// <summary>
    /// A write broke a unique index. Every provider throws this for a unique violation, with
    /// the provider's own exception attached as <see cref="Exception.InnerException"/>, so
    /// callers can tell which rule a racing writer hit without knowing which engine stores the
    /// data. <see cref="EntityType"/>, <see cref="PropertyNames"/> and <see cref="ColumnNames"/>
    /// name the entity and the key columns of the broken index. For a [DatabaseColumn(Unique = true)]
    /// index, <see cref="PropertyName"/> and <see cref="ColumnName"/> name its one column; for a
    /// unique [TableIndex], <see cref="DeclaredIndexName"/> is its declared name and they are null.
    /// When the index is not one Nucleus manages, such as a primary key or an index added by hand,
    /// the entity and names are null and the lists are empty. It is a <see cref="DatabaseException"/>
    /// of kind <see cref="DatabaseErrorKind.UniqueViolation"/>, never transient: the same write
    /// breaks the same index again.
    /// </summary>
    public class UniqueViolationException : DatabaseException
    {
        public UniqueViolationException(string message, string? indexName, Type? entityType, string? columnName, Exception innerException)
            : this(message, indexName, entityType, columnName, columnName, innerException)
        {
        }

        /// <summary>A broken [DatabaseColumn(Unique = true)] index, or one Nucleus does not manage when the names are null.</summary>
        public UniqueViolationException(string message, string? indexName, Type? entityType, string? propertyName, string? columnName, Exception innerException)
            : base(message, DatabaseErrorKind.UniqueViolation, transient: false, innerException)
        {
            IndexName = indexName;
            EntityType = entityType;
            PropertyName = propertyName;
            ColumnName = columnName;
            PropertyNames = propertyName == null ? [] : [propertyName];
            ColumnNames = columnName == null ? [] : [columnName];
        }

        /// <summary>A broken unique [TableIndex] index, declared as <paramref name="declaredIndexName"/>, over the given key properties and their columns.</summary>
        public UniqueViolationException(string message, string? indexName, Type? entityType, string declaredIndexName,
            IReadOnlyList<string> propertyNames, IReadOnlyList<string> columnNames, Exception innerException)
            : base(message, DatabaseErrorKind.UniqueViolation, transient: false, innerException)
        {
            ArgumentNullException.ThrowIfNull(declaredIndexName);
            ArgumentNullException.ThrowIfNull(propertyNames);
            ArgumentNullException.ThrowIfNull(columnNames);
            IndexName = indexName;
            EntityType = entityType;
            DeclaredIndexName = declaredIndexName;
            PropertyNames = propertyNames;
            ColumnNames = columnNames;
        }

        /// <summary>The Nucleus name of the broken index, or the engine's name when Nucleus does not manage it.</summary>
        public string? IndexName { get; }

        /// <summary>The entity type whose table holds the index, when Nucleus manages it.</summary>
        public Type? EntityType { get; }

        /// <summary>The property whose column a [DatabaseColumn(Unique = true)] index keys; null for a [TableIndex].</summary>
        public string? PropertyName { get; }

        /// <summary>The column a [DatabaseColumn(Unique = true)] index keys; null for a [TableIndex].</summary>
        public string? ColumnName { get; }

        /// <summary>The properties the index keys, in key order: one for a column's index, every key for a [TableIndex], none for an index Nucleus does not manage.</summary>
        public IReadOnlyList<string> PropertyNames { get; }

        /// <summary>The columns the index keys, in the order of <see cref="PropertyNames"/>.</summary>
        public IReadOnlyList<string> ColumnNames { get; }

        /// <summary>The name a unique [TableIndex] was declared with; null for a column's index or one Nucleus does not manage.</summary>
        public string? DeclaredIndexName { get; }

        /// <summary>
        /// True when the broken index is the one on <typeparamref name="T"/>'s <paramref name="name"/>,
        /// given as the property's name or its column's.
        /// </summary>
        public bool Is<T>(string name) =>
            EntityType == typeof(T) &&
            (string.Equals(PropertyName, name, StringComparison.OrdinalIgnoreCase) || string.Equals(ColumnName, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>True when the broken index is the [TableIndex] <paramref name="declaredName"/> on <typeparamref name="T"/>.</summary>
        public bool IsIndex<T>(string declaredName) =>
            EntityType == typeof(T) && string.Equals(DeclaredIndexName, declaredName, StringComparison.OrdinalIgnoreCase);
    }
}
