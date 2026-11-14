using System;

namespace TFM.Nucleus.Data
{
    /// <summary>
    /// Names an entity's table had before. When the schema is updated and the entity's table does
    /// not exist, a table under exactly one of these names is renamed to the entity's table name,
    /// rows, columns and indexes included, and then brought up to date like any existing table.
    /// </summary>
    /// <remarks>
    /// When more than one of the old tables exists, nothing is renamed and the schema update
    /// fails with the names it found: the rows of two tables cannot be merged safely, so which
    /// one to keep is a decision for a person. When the entity's table already exists, the old
    /// tables are left alone and a warning is logged. Not inherited, as each type has its own
    /// table. Nothing is renamed while the schema is read-only.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class LegacyTableNamesAttribute : Attribute
    {
        /// <param name="names">The old table names, prefix included.</param>
        public LegacyTableNamesAttribute(params string[] names)
        {
            Names = names ?? [];
        }

        /// <summary>The old table names, prefix included.</summary>
        public string[] Names { get; }
    }
}
