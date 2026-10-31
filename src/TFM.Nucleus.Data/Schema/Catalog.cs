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

    /// <summary>One key column of a Nucleus-named index as a provider's catalog reports it.</summary>
    public sealed class CatalogIndex
    {
        public string? TableName;
        public string? IndexName;
        public string? ColumnName;
        public bool IsUnique;
        public bool IsDescending;
        public bool HasFilter;
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
