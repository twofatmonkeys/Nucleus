using System;
using System.Collections.Generic;
using System.Linq;
using TFM.Nucleus.Data.Schema;

namespace TFM.Nucleus.Data.Providers
{
    /// <summary>
    /// The unique index a write broke, in Nucleus's terms: the entity whose table holds it, its
    /// Nucleus name, the name a [TableIndex] declared (null for a column's index), and its key
    /// properties and columns in key order.
    /// </summary>
    internal sealed record ResolvedUniqueIndex(
        Type EntityType,
        string IndexName,
        string? DeclaredIndexName,
        IReadOnlyList<string> PropertyNames,
        IReadOnlyList<string> ColumnNames);

    /// <summary>
    /// Finds which managed unique index an engine's index name belongs to, so a unique violation
    /// can name the entity and the rule it broke. It only runs when a write has already failed.
    /// </summary>
    internal static class UniqueIndexResolver
    {
        /// <summary>
        /// The unique index stored as <paramref name="physicalIndexName"/> on one of
        /// <paramref name="tables"/>, or null when Nucleus does not manage it. The prefix decides
        /// which kind of index to search, so a column's index costs no more to resolve because
        /// [TableIndex] indexes exist; a shortened name keeps its start, so the prefix survives.
        /// </summary>
        public static ResolvedUniqueIndex? Resolve(IEnumerable<TableSchema> tables, ISchemaDialect dialect, string? physicalIndexName)
        {
            if (physicalIndexName == null)
                return null;
            var comparer = dialect.IdentifierComparer;
            if (IndexNaming.IsTableIndexName(physicalIndexName))
            {
                foreach (var table in tables)
                    foreach (var index in table.TableIndexes)
                        if (index.Unique && comparer.Equals(SchemaComparer.PhysicalIndexName(table, index, dialect), physicalIndexName))
                            return new ResolvedUniqueIndex(table.ObjectSchema.Type, IndexNaming.TableIndexName(table, index), index.Name,
                                [.. index.Keys.Select(k => k.PropertyName)], [.. index.Keys.Select(k => k.ColumnName)]);
                return null;
            }
            foreach (var table in tables)
                foreach (var column in table.IndexedColumns.Where(c => c.Unique))
                    if (comparer.Equals(SchemaComparer.PhysicalIndexName(table, column, dialect), physicalIndexName))
                        return new ResolvedUniqueIndex(table.ObjectSchema.Type, IndexNaming.ColumnIndexName(table, column), null,
                            [column.PropertyName], [column.ColumnName]);
            return null;
        }
    }
}
