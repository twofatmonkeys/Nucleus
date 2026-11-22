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

        /// <summary>
        /// True when the table exists, every column exists with the type the schema needs, and a
        /// soft-deletable table has both of its soft-delete system columns.
        /// </summary>
        public bool IsCurrent => ExistsInDatabase && MissingSoftDeleteColumns.Count == 0 && Columns.All(c => c.IsCurrent);

        /// <summary>The soft-delete system columns the last compare found missing from the table.</summary>
        internal List<string> MissingSoftDeleteColumns { get; } = [];
        public bool HasFullTextSearch => Columns.Any(c => c.FullTextSearch);

        /// <summary>The type is [Deletable(DeleteBehavior.Soft)], so the table carries the soft-delete system columns.</summary>
        public bool IsSoftDeletable => SoftDelete.Applies(this);

        /// <summary>The columns that carry an index of their own.</summary>
        public IEnumerable<ColumnSchema> IndexedColumns => Columns.Where(c => c.HasColumnIndex);

        /// <summary>The indexes over several columns the type declares with [TableIndex], in declaration order.</summary>
        public IReadOnlyList<TableIndexSchema> TableIndexes { get; }

        /// <summary>The longest table name an entity can declare, the shortest limit of the supported engines before shortening.</summary>
        public const int MaxDeclaredNameLength = 128;

        private static readonly System.Text.RegularExpressions.Regex DeclaredNamePattern = new("^[A-Za-z_][A-Za-z0-9_]*$");

        /// <summary>
        /// The names the entity's table had before, from [LegacyTableNames], in declaration order.
        /// The schema update renames one of them to <see cref="TableName"/> when the table is missing.
        /// </summary>
        public IReadOnlyList<string> LegacyTableNames { get; }

        /// <summary>
        /// The full-name form of the table name (tbl_ and the type's full name), which is what the
        /// table was called before naming policies and [DatabaseTable] existed.
        /// </summary>
        public string FullTableName { get; }

        /// <summary>
        /// The logical table name of an entity type under the full-name policy: its [DatabaseTable]
        /// name if it has one, otherwise as its own [TableNaming] policy names it, otherwise tbl_
        /// and its full name with the separators replaced.
        /// </summary>
        public static string LogicalName(IObjectSchema objectSchema) => LogicalName(objectSchema, TableNamingPolicy.FullName);

        /// <summary>
        /// The logical table name of an entity type: its [DatabaseTable] name if it has one,
        /// otherwise as its own (or an inherited) [TableNaming] policy names it, otherwise as
        /// <paramref name="providerPolicy"/> names it.
        /// </summary>
        public static string LogicalName(IObjectSchema objectSchema, TableNamingPolicy providerPolicy)
        {
            ArgumentNullException.ThrowIfNull(objectSchema);
            ArgumentNullException.ThrowIfNull(providerPolicy);
            var type = objectSchema.Type;
            if (type.GetCustomAttributes(typeof(DatabaseTableAttribute), inherit: false).FirstOrDefault() is DatabaseTableAttribute table)
            {
                EnsureDeclaredName(table.Name, $"[DatabaseTable] on '{objectSchema.FullName}'");
                return table.Name;
            }
            if (type.GetCustomAttributes(typeof(TableNamingAttribute), inherit: true).FirstOrDefault() is TableNamingAttribute naming)
            {
                if (naming.Style == TableNamingStyle.ObjectName && naming.Levels < 1)
                    throw new InvalidOperationException(
                        $"[TableNaming] on '{objectSchema.FullName}' (or a type it derives from) keeps {naming.Levels} levels; ObjectName needs at least 1.");
                return TableNamingPolicy.From(naming).NameFor(type);
            }
            return providerPolicy.NameFor(type);
        }

        // A declared name is written into statements as it is, so it is held to the characters
        // every engine accepts in an unquoted identifier.
        private static void EnsureDeclaredName(string? name, string where)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MaxDeclaredNameLength || !DeclaredNamePattern.IsMatch(name))
                throw new InvalidOperationException(
                    $"{where} declares the table name '{name}'. A table name is letters, digits and underscores, " +
                    $"starts with a letter or underscore, and is at most {MaxDeclaredNameLength} characters.");
        }

        public TableSchema(IObjectSchema objectSchema) : this(objectSchema, TableNamingPolicy.FullName)
        {
        }

        /// <summary>The table of <paramref name="objectSchema"/>, named by its attributes or else by <paramref name="namingPolicy"/>.</summary>
        public TableSchema(IObjectSchema objectSchema, TableNamingPolicy namingPolicy)
        {
            ArgumentNullException.ThrowIfNull(objectSchema);
            ArgumentNullException.ThrowIfNull(namingPolicy);
            TableName = LogicalName(objectSchema, namingPolicy);
            FullTableName = TableNamingPolicy.FullName.NameFor(objectSchema.Type);
            LegacyTableNames = BuildLegacyTableNames(objectSchema, TableName);
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
            EnsureColumnNamesAreUsable(objectSchema);
            TableIndexes = BuildTableIndexes(objectSchema);
            SelfReferences = objectSchema.HasLinkedObjects
                ? [.. (objectSchema.LinkedObjectProperties ?? []).Where(l => l.IsDBColumn && l.Type.IsAssignableFrom(objectSchema.Type))]
                : [];
        }

        /// <summary>
        /// The stored [LinkedObject] properties that can hold a row of this table: typed as the
        /// type itself or as one it derives from. A batch that inserts rows pointing at each other
        /// through them is ordered so a row is inserted after the row it points at. Empty for
        /// most types, which is what the save checks first.
        /// </summary>
        internal IReadOnlyList<IPropertySchema> SelfReferences { get; }

        /// <summary>
        /// The [LegacyTableNames] of the type, each a usable table name, none repeated and none the
        /// table's own name, which would rename the table onto itself.
        /// </summary>
        private static List<string> BuildLegacyTableNames(IObjectSchema objectSchema, string tableName)
        {
            if (objectSchema.Type.GetCustomAttributes(typeof(LegacyTableNamesAttribute), inherit: false).FirstOrDefault() is not LegacyTableNamesAttribute attribute)
                return [];
            var where = $"[LegacyTableNames] on '{objectSchema.FullName}'";
            List<string> names = [];
            foreach (var name in attribute.Names)
            {
                EnsureDeclaredName(name, where);
                if (name.Equals(tableName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"{where} lists '{name}', which is the table's own name.");
                if (names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"{where} lists '{name}' more than once.");
                names.Add(name);
            }
            return names;
        }

        /// <summary>
        /// The [TableIndex] declarations as indexes over this table's columns. A name used twice,
        /// a property that is not a stored column, a column named twice in one index, or a long
        /// text key fails here at schema build, not at the first CREATE INDEX.
        /// </summary>
        private List<TableIndexSchema> BuildTableIndexes(IObjectSchema objectSchema)
        {
            var indexes = new List<TableIndexSchema>();
            var attributes = objectSchema.Type.GetCustomAttributes(typeof(TableIndexAttribute), inherit: true).Cast<TableIndexAttribute>();
            foreach (var attribute in attributes.OrderBy(a => a.Name, StringComparer.Ordinal))
            {
                string Where() => $"[TableIndex(\"{attribute.Name}\")] on '{objectSchema.FullName}'";
                if (!TableIndexSchema.IsName(attribute.Name))
                    throw new InvalidOperationException($"{Where()} needs a name of letters, digits and underscores.");
                if (indexes.Any(i => i.Name.Equals(attribute.Name, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"{Where()} uses a name another [TableIndex] on the type already has.");
                if (attribute.Keys.Length == 0)
                    throw new InvalidOperationException($"{Where()} needs at least one key property.");
                ColumnSchema Column(string property) => FindByPropertyName(property)
                    ?? throw new InvalidOperationException($"{Where()} names '{property}', which is not a stored column of the type.");
                var keys = attribute.Keys.Select(Column).ToList();
                var include = (attribute.Include ?? []).Select(Column).ToList();
                var all = keys.Concat(include).ToList();
                if (all.Distinct().Count() != all.Count)
                    throw new InvalidOperationException($"{Where()} names a column more than once.");
                if (keys.FirstOrDefault(k => k.IsMax) is { } longKey)
                    throw new InvalidOperationException($"{Where()} keys on '{longKey.PropertyName}', a long text column, which cannot be an index key; include it instead.");
                indexes.Add(new TableIndexSchema(attribute.Name, keys, include, attribute.Unique));
            }
            return indexes;
        }

        /// <summary>
        /// Column names come from [DatabaseColumn]'s ColumnName, so two properties can claim one
        /// column, or a property can claim a column the table already has for itself. Both are
        /// refused here. Names are compared without regard to case, as SQL Server compares them,
        /// so an entity is valid on every provider. The columns CoreObject provides keep their
        /// own names, which every provider writes literally.
        /// </summary>
        private void EnsureColumnNamesAreUsable(IObjectSchema objectSchema)
        {
            foreach (var system in new[] { "ID", TimestampColumn })
                if (FindByPropertyName(system) is { } column && !column.ColumnName.Equals(system, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Type '{objectSchema.FullName}' names the column of '{system}' '{column.ColumnName}'. " +
                        $"The {system} column CoreObject provides keeps its name.");

            var reserved = new List<string> { ClusterIdColumn };
            if (IsSoftDeletable)
                reserved.AddRange([SoftDelete.IsDeletedColumn, SoftDelete.DeletedAtColumn]);
            foreach (var column in _columns)
                if (reserved.Contains(column.ColumnName, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Property '{column.PropertyName}' on '{objectSchema.FullName}' maps to column '{column.ColumnName}', " +
                        "which the table already has as a system column.");

            var duplicate = _columns.GroupBy(c => c.ColumnName, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (duplicate != null)
                throw new InvalidOperationException(
                    $"Type '{objectSchema.FullName}' maps more than one property to column '{duplicate.Key}': " +
                    $"{string.Join(", ", duplicate.Select(c => c.PropertyName))}. Column names are compared without regard to case.");
        }

        public ColumnSchema? FindByPropertyName(string propertyName) =>
            _byProperty.TryGetValue(propertyName, out var columnSchema) ? columnSchema : null;

        public ColumnSchema? FindByColumnName(string columnName) =>
            _columns.FirstOrDefault(x => x.ColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase));

        public bool TryGetValueByColumn(string columnName, out IDataColumnSchema? dataTableSchema) =>
            (dataTableSchema = FindByColumnName(columnName)) != null;
        public bool TryGetValueByProperty(string propertyName, out IDataColumnSchema? dataTableSchema) =>
            (dataTableSchema = FindByPropertyName(propertyName)) != null;

        /// <summary>The column of a property, by the property's name.</summary>
        public ColumnSchema this[string propertyName] => _byProperty[propertyName];

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

        /// <summary>The prefix of an index over several columns declared with [TableIndex].</summary>
        public const string TableIndexPrefix = "NIX_";

        /// <summary>The logical name of a column's index: the prefix, the table and the column.</summary>
        public static string ColumnIndexName(TableSchema table, ColumnSchema column) => ColumnIndexName(table.TableName, column);

        /// <summary>The logical name a column's index has on the table named <paramref name="tableName"/>.</summary>
        public static string ColumnIndexName(string tableName, ColumnSchema column) =>
            $"{(column.Unique ? UniqueIndexPrefix : IndexPrefix)}{tableName}_{column.ColumnName}";

        /// <summary>The logical name of a [TableIndex] index: the prefix, the table and the index's own name.</summary>
        public static string TableIndexName(TableSchema table, TableIndexSchema index) => TableIndexName(table.TableName, index);

        /// <summary>The logical name a [TableIndex] index has on the table named <paramref name="tableName"/>.</summary>
        public static string TableIndexName(string tableName, TableIndexSchema index) =>
            $"{TableIndexPrefix}{tableName}_{index.Name}";

        /// <summary>True when a stored index name is one of the [TableIndex] indexes Nucleus manages.</summary>
        public static bool IsTableIndexName(string? indexName) =>
            indexName != null && indexName.StartsWith(TableIndexPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An index over several columns of one table, from a [TableIndex] declaration: the key
    /// columns in order, then the columns it carries without ordering by them.
    /// </summary>
    public sealed class TableIndexSchema
    {
        private static readonly System.Text.RegularExpressions.Regex NamePattern = new("^[A-Za-z0-9_]{1,100}$");

        public TableIndexSchema(string name, IReadOnlyList<ColumnSchema> keys, IReadOnlyList<ColumnSchema> include)
            : this(name, keys, include, unique: false)
        {
        }

        public TableIndexSchema(string name, IReadOnlyList<ColumnSchema> keys, IReadOnlyList<ColumnSchema> include, bool unique)
        {
            Name = name;
            Keys = keys;
            Include = include;
            Unique = unique;
        }

        /// <summary>
        /// True when no two rows may share every key value. The index then covers only rows whose
        /// keys are all non-null, as a column's unique index covers only non-null values.
        /// </summary>
        public bool Unique { get; }

        /// <summary>The index's own name, as declared.</summary>
        public string Name { get; }

        /// <summary>The columns the index is ordered by, first to last, each ascending.</summary>
        public IReadOnlyList<ColumnSchema> Keys { get; }

        /// <summary>The columns carried in the index, in declaration order.</summary>
        public IReadOnlyList<ColumnSchema> Include { get; }

        /// <summary>True when <paramref name="name"/> can be an index's own name.</summary>
        public static bool IsName(string? name) => name != null && NamePattern.IsMatch(name);
    }
}
