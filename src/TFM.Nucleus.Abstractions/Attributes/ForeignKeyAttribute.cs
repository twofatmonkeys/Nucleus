using System;

namespace TFM.Nucleus.Data
{
    /// <summary>
    /// A foreign key on a stored column, for a reference the schema cannot see, or to change or
    /// drop the key Nucleus gives a reference it can.
    /// </summary>
    /// <remarks>
    /// Nucleus gives a key, unasked, to a child's parent column, to a stored <c>[LinkedObject]</c>
    /// column and to the key a <c>[LinkedBy]</c> collection names, each to the ID of the table it
    /// points at. This attribute is for the rest: a plain <see cref="Guid"/> column that holds the
    /// ID of another entity (<c>[ForeignKey(typeof(Keeper))]</c>). On a column that already gets a
    /// key it sets <see cref="OnDelete"/>, and <c>[ForeignKey(false)]</c> drops the key. A key is
    /// created only between two types the same provider stores, and every key column is indexed,
    /// so a delete of the row it points at does not scan the table.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class ForeignKeyAttribute : Attribute
    {
        /// <summary>Keeps the key Nucleus gives the column, with the options set here.</summary>
        public ForeignKeyAttribute()
        {
            Enabled = true;
        }

        /// <summary>A key from this <see cref="Guid"/> column to the ID of <paramref name="referencedType"/>'s table.</summary>
        public ForeignKeyAttribute(Type referencedType)
        {
            ArgumentNullException.ThrowIfNull(referencedType);
            ReferencedType = referencedType;
            Enabled = true;
        }

        /// <summary>
        /// <c>false</c> leaves the column without the key Nucleus would give it. A child's parent
        /// column always has its key.
        /// </summary>
        public ForeignKeyAttribute(bool enabled)
        {
            Enabled = enabled;
        }

        /// <summary>The entity whose table the key points at; null to take it from the reference.</summary>
        public Type? ReferencedType { get; }

        /// <summary>False when the column has no key.</summary>
        public bool Enabled { get; }

        /// <summary>What the database does to this column when the row it points at is deleted.</summary>
        public ForeignKeyAction OnDelete { get; set; } = ForeignKeyAction.NoAction;
    }

    /// <summary>What the database does to a referencing row when the row it points at is deleted.</summary>
    /// <remarks>
    /// There is no cascade: Nucleus deletes owned rows itself, keeping each type's hard or soft
    /// rule, and a database cascade would delete rows behind it.
    /// </remarks>
    public enum ForeignKeyAction
    {
        /// <summary>The delete fails while a row points at the row it deletes.</summary>
        NoAction,

        /// <summary>The database sets the referencing column to null. Not allowed on a child's parent column.</summary>
        SetNull,
    }
}
