using System;

namespace TFM.Nucleus.Data
{
    /// <summary>
    /// An index over several columns of an entity's table, kept in step with the entity like a
    /// column's own <see cref="DatabaseColumnAttribute.Indexed"/> index: created when missing,
    /// recreated when its definition changes, dropped when the attribute is removed, and never a
    /// reason to rebuild the table. Name the properties with <c>nameof</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="Keys"/> are the columns the index is ordered by, in that order: a read that
    /// filters on the first key, or on the first and a range of the second, finds its rows
    /// without scanning the table. <see cref="Include"/> columns are carried in the index
    /// without ordering it, so a read of only key and included columns never touches the table
    /// (a covering index). Key columns cannot be long text; included ones can.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
    public sealed class TableIndexAttribute : Attribute
    {
        /// <param name="name">
        /// The index's own name, unique on the table: letters, digits and underscores. The stored
        /// name is NIX_, the table name and this.
        /// </param>
        /// <param name="keys">The properties the index is ordered by, first to last.</param>
        public TableIndexAttribute(string name, params string[] keys)
        {
            Name = name;
            Keys = keys ?? [];
        }

        /// <summary>The index's own name, unique on the table.</summary>
        public string Name { get; }

        /// <summary>The properties the index is ordered by, first to last.</summary>
        public string[] Keys { get; }

        /// <summary>The properties carried in the index without ordering it, so reads of them are answered from the index alone.</summary>
        public string[] Include { get; set; } = [];

        /// <summary>
        /// True when no two rows may share the same values of every key, the rule a single column's
        /// <see cref="DatabaseColumnAttribute.Unique"/> gives one column, over all of
        /// <see cref="Keys"/> together.
        /// </summary>
        /// <remarks>
        /// A row is held to the rule only when every key has a value: a row with any key left null is
        /// not constrained, on every engine. <see cref="Include"/> columns may be carried as well. A
        /// write that breaks the rule throws <see cref="UniqueViolationException"/> naming the entity
        /// and the index (<see cref="UniqueViolationException.IsIndex{T}"/>). Turning it on or off
        /// drops and creates the index again; if existing rows already break it, initialisation fails
        /// with a logged error naming the table and index, and no rows are changed.
        /// </remarks>
        public bool Unique { get; set; }
    }
}
