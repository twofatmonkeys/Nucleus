using System.Collections.Generic;

namespace TFM.Nucleus.Data.Schema
{
    /// <summary>One column as a provider's catalog reports it. The type name is the engine's own.</summary>
    public sealed class CatalogColumn
    {
        public string? Name;
        public string? TableName;
        public string? TypeName;
        public int DataLength = 0;
        public int Precision = 0;
        public int Scale = 0;
        public bool IsNullable = true;
    }

    /// <summary>
    /// One column of a Nucleus-named index as a provider's catalog reports it. A column index
    /// has one key column; a [TableIndex] index has its keys, numbered by
    /// <see cref="KeyOrdinal"/> from 1, and its included columns, which have
    /// <see cref="IsIncluded"/> set.
    /// </summary>
    public sealed class CatalogIndex
    {
        public string? TableName;
        public string? IndexName;
        public string? ColumnName;
        public bool IsUnique;
        public bool IsDescending;
        public bool HasFilter;

        /// <summary>The key column's place in the index, from 1; 0 for an included column.</summary>
        public int KeyOrdinal;

        /// <summary>The column is carried by the index without ordering it.</summary>
        public bool IsIncluded;
    }

    /// <summary>
    /// One Nucleus-named foreign key as a provider's catalog reports it: a single column of one
    /// table holding the ID of a row of another. Names are the engine's physical names.
    /// </summary>
    public sealed class CatalogForeignKey
    {
        public string? TableName;
        public string? KeyName;
        public string? ColumnName;
        public string? ReferencedTableName;
        public string? ReferencedColumnName;
        public ForeignKeyAction OnDelete;

        /// <summary>
        /// False when the engine has not checked the rows that were there when the key was
        /// created, because some of them pointed at rows that did not exist.
        /// </summary>
        public bool IsChecked = true;
    }

    /// <summary>What a provider found in the database: table names, columns and Nucleus-named indexes.</summary>
    public sealed record CatalogSnapshot(
        IReadOnlyList<string> Tables,
        IReadOnlyList<CatalogColumn> Columns,
        IReadOnlyList<CatalogIndex> Indexes);

    /// <summary>A statement a schema change runs, with the parameters it binds.</summary>
    public sealed record SchemaStatement(string Text, ParameterCollection? Parameters = null)
    {
        public static implicit operator SchemaStatement(string text) => new(text);

        public override string ToString() => Text;
    }
}
