using System;

namespace TFM.Nucleus.Data
{
    /// <summary>
    /// A write broke a foreign key: a row was written pointing at a row that does not exist, or a
    /// row was deleted while a live row still points at it. Every provider throws this for a key
    /// violation, with the provider's own exception attached as <see cref="Exception.InnerException"/>.
    /// <see cref="EntityType"/>, <see cref="PropertyName"/> and <see cref="ColumnName"/> name the
    /// reference whose key was broken, which is the side that points: on a refused delete, the
    /// entity still pointing at the deleted row, not the one deleted. <see cref="ReferencedType"/>
    /// is the entity it points at. The names are null when the engine does not name the key, as
    /// SQLite does not, or when the key is not one Nucleus keeps. It is a <see cref="DatabaseException"/>
    /// of kind <see cref="DatabaseErrorKind.ForeignKeyViolation"/>, never transient: the same write
    /// breaks the same key again.
    /// </summary>
    public class ForeignKeyViolationException : DatabaseException
    {
        public ForeignKeyViolationException(string message, string? keyName, Type? entityType, string? propertyName, string? columnName,
            Type? referencedType, Exception innerException)
            : base(message, DatabaseErrorKind.ForeignKeyViolation, transient: false, innerException)
        {
            KeyName = keyName;
            EntityType = entityType;
            PropertyName = propertyName;
            ColumnName = columnName;
            ReferencedType = referencedType;
        }

        /// <summary>The Nucleus name of the broken key, or the engine's name when Nucleus does not keep it; null when the engine does not say.</summary>
        public string? KeyName { get; }

        /// <summary>The entity type whose column holds the reference, when Nucleus keeps the key.</summary>
        public Type? EntityType { get; }

        /// <summary>The property holding the reference, when Nucleus keeps the key.</summary>
        public string? PropertyName { get; }

        /// <summary>The column holding the reference, when Nucleus keeps the key.</summary>
        public string? ColumnName { get; }

        /// <summary>The entity type the reference points at, when Nucleus keeps the key.</summary>
        public Type? ReferencedType { get; }

        /// <summary>
        /// True when the broken key is the one on <typeparamref name="T"/>'s <paramref name="name"/>,
        /// given as the property's name or its column's. Always false when the engine does not name
        /// the key.
        /// </summary>
        public bool Is<T>(string name) =>
            EntityType == typeof(T) &&
            (string.Equals(PropertyName, name, StringComparison.OrdinalIgnoreCase) || string.Equals(ColumnName, name, StringComparison.OrdinalIgnoreCase));
    }
}
