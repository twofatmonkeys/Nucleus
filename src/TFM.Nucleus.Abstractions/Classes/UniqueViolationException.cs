using System;

namespace TFM.Nucleus.Data
{
    /// <summary>
    /// A write broke a unique index. Every provider throws this for a unique violation, with
    /// the provider's own exception attached as <see cref="Exception.InnerException"/>, so
    /// callers can tell which rule a racing writer hit without knowing which engine stores the
    /// data. <see cref="EntityType"/>, <see cref="PropertyName"/> and <see cref="ColumnName"/>
    /// name the [DatabaseColumn] property whose index was broken; all are null when the index is
    /// not one Nucleus manages, such as a primary key or an index added by hand.
    /// </summary>
    public class UniqueViolationException : Exception
    {
        public UniqueViolationException(string message, string? indexName, Type? entityType, string? columnName, Exception innerException)
            : this(message, indexName, entityType, columnName, columnName, innerException)
        {
        }

        public UniqueViolationException(string message, string? indexName, Type? entityType, string? propertyName, string? columnName, Exception innerException)
            : base(message, innerException)
        {
            IndexName = indexName;
            EntityType = entityType;
            PropertyName = propertyName;
            ColumnName = columnName;
        }

        /// <summary>The Nucleus name of the broken index, or the engine's name when Nucleus does not manage it.</summary>
        public string? IndexName { get; }

        /// <summary>The entity type whose table holds the index, when Nucleus manages it.</summary>
        public Type? EntityType { get; }

        /// <summary>The property whose column the index keys, when Nucleus manages it.</summary>
        public string? PropertyName { get; }

        /// <summary>The column the index keys, when Nucleus manages it.</summary>
        public string? ColumnName { get; }

        /// <summary>
        /// True when the broken index is the one on <typeparamref name="T"/>'s <paramref name="name"/>,
        /// given as the property's name or its column's.
        /// </summary>
        public bool Is<T>(string name) =>
            EntityType == typeof(T) &&
            (string.Equals(PropertyName, name, StringComparison.OrdinalIgnoreCase) || string.Equals(ColumnName, name, StringComparison.OrdinalIgnoreCase));
    }
}
