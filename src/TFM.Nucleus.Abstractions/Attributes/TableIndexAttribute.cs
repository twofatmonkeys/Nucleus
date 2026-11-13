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
    }
}
