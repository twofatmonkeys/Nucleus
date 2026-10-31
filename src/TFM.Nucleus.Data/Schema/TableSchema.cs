using System;
using System.Collections.Generic;
using System.Linq;
using TFM.Nucleus.Data.Queries;

namespace TFM.Nucleus.Data.Schema
{
    /// <summary>
    /// The table of one entity type: its logical name, its columns in the order
    /// <see cref="IObjectSchema.AllDBColumns"/> lists them (which is the column order of every
    /// select and every <see cref="DataSet"/>), and the comparison state the schema check
    /// records. Providers map the logical name to a physical one when their engine needs to.
    /// </summary>
    public class TableSchema : IDataTableSchema, IEquatable<TableSchema>
    {
        /// <summary>The system column that orders rows physically and orders a rebuild's copy.</summary>
        public const string ClusterIdColumn = "ClusterID";

        /// <summary>The column every entity inherits, set by the server on every write.</summary>
        public const string TimestampColumn = "Timestamp";

        public readonly string TableName;
        public readonly IObjectSchema ObjectSchema;
        private readonly Dictionary<string, ColumnSchema> _byProperty = [];
        private readonly List<ColumnSchema> _columns = [];

        public IReadOnlyList<ColumnSchema> Columns => _columns;
        public ColumnSchema IdentityColumn { get; }
        public bool ExistsInDatabase { get; internal set; } = false;
        public bool IsCurrent => ExistsInDatabase && Columns.All(c => c.IsCurrent);
        public bool HasFullTextIndex => Columns.Any(c => c.FullTextIndex);

        /// <summary>The type is [Deletable(DeleteBehavior.Soft)], so the table carries the soft-delete system columns.</summary>
        public bool IsSoftDeletable => SoftDelete.Applies(this);

        /// <summary>The columns that carry an index of their own.</summary>
        public IEnumerable<ColumnSchema> IndexedColumns => Columns.Where(c => c.HasColumnIndex);

        /// <summary>The logical table name of an entity type: tbl_ and its full name, separators replaced.</summary>
        public static string LogicalName(IObjectSchema objectSchema) =>
            $"tbl_{objectSchema.FullName.Replace('.', '_').Replace('+', '_')}";

        public TableSchema(IObjectSchema objectSchema)
        {
            ArgumentNullException.ThrowIfNull(objectSchema);
            TableName = LogicalName(objectSchema);
            ObjectSchema = objectSchema;
            foreach (var propertySchema in objectSchema.AllDBColumns)
            {
                var column = new ColumnSchema(propertySchema, objectSchema);
                _byProperty[propertySchema.Name] = column;
                _columns.Add(column);
            }

            // The entity model is built around the Guid ID column CoreObject provides: the
            // generated default, Get by ID and the cache index all assume it. The PrimaryKey flag
            // is therefore reserved for ID, and a type that marks anything else, or nothing,
            // fails here at schema build rather than at the first update or delete.
            var pkColumns = _columns.Where(c => c.PrimaryKey).ToList();
            if (pkColumns.Count != 1)
                throw new InvalidOperationException(
                    $"Type '{objectSchema.FullName}' must have exactly one [DatabaseColumn(PrimaryKey = true)] property, found {pkColumns.Count}" +
                    (pkColumns.Count == 0 ? "." : $": {string.Join(", ", pkColumns.Select(c => c.PropertySchema.Name))}.") +
                    " The primary key is the ID column CoreObject provides.");
            if (!pkColumns[0].ColumnName.Equals("ID", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Type '{objectSchema.FullName}' marks '{pkColumns[0].PropertySchema.Name}' as PrimaryKey. " +
                    "The primary key is reserved for the ID column CoreObject provides.");
            IdentityColumn = pkColumns[0];
        }

        public ColumnSchema? FindByPropertyName(string propertyName) =>
            _byProperty.TryGetValue(propertyName, out var columnSchema) ? columnSchema : null;

        public ColumnSchema? FindByColumnName(string columnName) =>
            _columns.FirstOrDefault(x => x.ColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase));

        public bool TryGetValueByColumn(string columnName, out IDataColumnSchema? dataTableSchema) =>
            (dataTableSchema = FindByColumnName(columnName)) != null;
        public bool TryGetValueByProperty(string propertyName, out IDataColumnSchema? dataTableSchema) =>
            (dataTableSchema = FindByPropertyName(propertyName)) != null;

        public ColumnSchema this[string columnName] => _byProperty[columnName];

        public bool Equals(TableSchema? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return string.Equals(TableName, other.TableName, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object? obj) => Equals(obj as TableSchema);

        public override int GetHashCode() =>
            TableName.ToUpperInvariant().GetHashCode();

        #region IDataTableSchema
        string IDataTableSchema.TableName => TableName;
        IObjectSchema IDataTableSchema.ObjectSchema => ObjectSchema;
        IEnumerable<IDataColumnSchema> IDataTableSchema.Columns => Columns;
        IDataColumnSchema? IDataTableSchema.IdentityColumn => IdentityColumn;
        IDataColumnSchema? IDataTableSchema.FindByColumnName(string columnName) => FindByColumnName(columnName);
        IDataColumnSchema? IDataTableSchema.FindByPropertyName(string propertyName) => FindByPropertyName(propertyName);
        IDataColumnSchema IDataTableSchema.this[string columnName] => this[columnName];
        #endregion

        public override string ToString()
        {
            return TableName ?? "null";
        }
    }

    /// <summary>
    /// The names Nucleus gives the column indexes it manages. The schema compare and sync only
    /// look at, drop or create indexes carrying one of these prefixes, so indexes a DBA adds by
    /// hand are never touched.
    /// </summary>
    public static class IndexNaming
    {
        public const string IndexPrefix = "NCI_";
        public const string UniqueIndexPrefix = "UQ_";

        /// <summary>The logical name of a column's index: the prefix, the table and the column.</summary>
        public static string ColumnIndexName(TableSchema table, ColumnSchema column) =>
            $"{(column.Unique ? UniqueIndexPrefix : IndexPrefix)}{table.TableName}_{column.ColumnName}";
    }
}
