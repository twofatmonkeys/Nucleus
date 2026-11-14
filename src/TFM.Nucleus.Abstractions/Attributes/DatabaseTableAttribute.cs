using System;

namespace TFM.Nucleus.Data
{
    /// <summary>
    /// The name of an entity's table, used as written in place of the name the naming policy
    /// would give it. The name is the whole table name, prefix included: letters, digits and
    /// underscores, starting with a letter or underscore, at most 128 characters.
    /// </summary>
    /// <remarks>
    /// Not inherited: an entity and the entities derived from it each have a table of their own,
    /// so a name inherited from a base type would give two types one table. Changing the name of
    /// an existing table leaves the old table where it is unless the old name is listed in
    /// <see cref="LegacyTableNamesAttribute"/>, or the old name is the full-name form and the
    /// provider renames full-name tables.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class DatabaseTableAttribute : Attribute
    {
        /// <param name="name">The table name, prefix included.</param>
        public DatabaseTableAttribute(string name)
        {
            Name = name;
        }

        /// <summary>The table name, prefix included.</summary>
        public string Name { get; }
    }
}
