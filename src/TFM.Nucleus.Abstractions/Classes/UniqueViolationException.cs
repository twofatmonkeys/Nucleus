using System;

namespace TFM.Nucleus.Data
{
    /// <summary>
    /// A write broke a unique index. Every provider throws this for a unique violation, with
    /// the provider's own exception attached as <see cref="Exception.InnerException"/>, so
    /// callers can tell which rule a racing writer hit without knowing which engine stores the
    /// data. <see cref="EntityType"/> and <see cref="ColumnName"/> name the [DatabaseColumn]
    /// property whose index was broken; both are null when the index is not one Nucleus manages,
    /// such as a primary key or an index added by hand.
    /// </summary>
    public class UniqueViolationException : Exception
    {
        public UniqueViolationException(string message, string? indexName, Type? entityType, string? columnName, Exception innerException)
            : base(message, innerException)
        {
            IndexName = indexName;
            EntityType = entityType;
            ColumnName = columnName;
        }

        /// <summary>The Nucleus name of the broken index, or the engine's name when Nucleus does not manage it.</summary>
        public string? IndexName { get; }

        /// <summary>The entity type whose table holds the index, when Nucleus manages it.</summary>
        public Type? EntityType { get; }

        /// <summary>The column the index keys, when Nucleus manages it.</summary>
        public string? ColumnName { get; }

        /// <summary>True when the broken index is the one on <typeparamref name="T"/>'s <paramref name="columnName"/> column.</summary>
        public bool Is<T>(string columnName) =>
            EntityType == typeof(T) && string.Equals(ColumnName, columnName, StringComparison.OrdinalIgnoreCase);
    }
}
