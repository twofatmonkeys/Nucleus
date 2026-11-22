using System;

namespace TFM.Nucleus.Data.Schema
{
    /// <summary>
    /// One foreign key Nucleus keeps: <see cref="Column"/> of <see cref="Table"/> holds the ID of a
    /// row of <see cref="Referenced"/>. Built by <see cref="SchemaManager.BuildSchema"/> from the
    /// entity model, created and dropped by the schema sync like a column index.
    /// </summary>
    public sealed class ForeignKeySchema
    {
        internal ForeignKeySchema(TableSchema table, ColumnSchema column, TableSchema referenced, ForeignKeyAction onDelete, ForeignKeySource source)
        {
            Table = table;
            Column = column;
            Referenced = referenced;
            OnDelete = onDelete;
            Source = source;
            Name = IndexNaming.ForeignKeyName(table.TableName, column);
        }

        /// <summary>The table whose column holds the reference.</summary>
        public TableSchema Table { get; }

        /// <summary>The column holding the referenced row's ID.</summary>
        public ColumnSchema Column { get; }

        /// <summary>The table whose ID the column holds.</summary>
        public TableSchema Referenced { get; }

        public ForeignKeyAction OnDelete { get; }

        /// <summary>What in the entity model gives the column its key.</summary>
        public ForeignKeySource Source { get; }

        /// <summary>The logical name: FK_, the table and the column.</summary>
        public string Name { get; }

        public override string ToString() => $"{Name} -> {Referenced.TableName}";
    }

    /// <summary>What in the entity model gives a column its foreign key.</summary>
    public enum ForeignKeySource
    {
        /// <summary>A child's parent column, to its parent's table.</summary>
        Parent,

        /// <summary>A stored [LinkedObject] column, to the table of the type it holds.</summary>
        LinkedObject,

        /// <summary>The key a [LinkedBy] collection names, to the one's table.</summary>
        LinkedBy,

        /// <summary>A [ForeignKey(typeof(T))] on a plain Guid column.</summary>
        Declared,
    }
}
