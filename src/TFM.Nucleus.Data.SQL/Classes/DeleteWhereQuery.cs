using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TFM.Nucleus.Data.SQL
{
    /// <summary>
    /// Deletes every row of a [Deletable] type that matches a WHERE, without loading any of them,
    /// and cascades to the rows of its child collections by parent ID. The batch deletes the
    /// deepest children first, so each child statement still finds the parent rows it selects
    /// by, and ends with <c>SELECT @@ROWCOUNT</c> straight after the root statement, which is how
    /// the caller learns how many rows of the root type were affected.
    /// <para>
    /// Each type keeps its own delete behaviour: a hard type is deleted, a soft type is flagged.
    /// A child selects by the same condition its parent's statement uses, soft filter included,
    /// so it is removed with exactly the parents this batch removes.
    /// </para>
    /// <para>
    /// The batch runs as one command through <see cref="SqlDataContext"/>'s dedicated path, never
    /// the generic one: that path regroups queries by table and detaches parents, which would
    /// break the subqueries and the order. Further chunks of an ID list hang off
    /// <see cref="Query.LinkedQueries"/> and run as their own commands in the same context.
    /// </para>
    /// </summary>
    public class DeleteWhereQuery : Query, IDeleteQuery
    {
        /// <summary>IDs per statement when deleting a list of objects, well under the parameter limit.</summary>
        internal const int IdsPerStatement = 1000;

        private readonly Target _root;

        /// <summary>One table in the cascade, with the column that links it to its parent.</summary>
        private sealed class Target(SqlTableSchema table, Target? parent, string? parentColumn)
        {
            public SqlTableSchema Table { get; } = table;
            public Target? Parent { get; } = parent;
            public string? ParentColumn { get; } = parentColumn;
            public List<Target> Children { get; } = [];
            public Parameter[] SetParameters { get; } = SoftDelete.Applies(table) ? SoftDelete.NewSetParameters() : [];
            public bool IsSoft => SetParameters.Length > 0;
        }

        /// <param name="tableSchema">The table of the type being deleted.</param>
        /// <param name="resolveTable">
        /// Finds the table of a child type, or null when this provider does not own it.
        /// </param>
        public DeleteWhereQuery(SqlTableSchema tableSchema, Func<Type, SqlTableSchema?> resolveTable)
        {
            ArgumentNullException.ThrowIfNull(tableSchema);
            ArgumentNullException.ThrowIfNull(resolveTable);
            if (!tableSchema.ObjectSchema.IsDeletable)
                throw new NotSupportedException($"Type {tableSchema.ObjectSchema.FullName} is not [Deletable]; deletion is opt-in.");
            Type = QueryType.DELETE;
            TableName = tableSchema.TableName;
            TableSchema = tableSchema;
            ObjectType = tableSchema.ObjectSchema.Type;
            _root = new Target(tableSchema, null, null);
            AddChildren(_root, resolveTable, [tableSchema.ObjectSchema.Type]);
            foreach (var target in Targets(_root))
                SetParameters.AddRange(target.SetParameters);
        }

        /// <summary>
        /// Deletes the given IDs of one type with their child rows: one query per
        /// <see cref="IdsPerStatement"/> IDs, the rest linked. Null when there are no IDs.
        /// </summary>
        public static DeleteWhereQuery? ForIds(SqlTableSchema tableSchema, Func<Type, SqlTableSchema?> resolveTable, IEnumerable<Guid> ids)
        {
            DeleteWhereQuery? root = null;
            foreach (var chunk in ids.Distinct().Chunk(IdsPerStatement))
            {
                var query = new DeleteWhereQuery(tableSchema, resolveTable);
                query.Where.And(new Parameter(tableSchema.IdentityColumn.ColumnName, chunk.ToList(), Operator.In));
                if (root == null)
                    root = query;
                else
                    root.LinkedQueries.Add(query);
            }
            return root;
        }

        /// <summary>
        /// Adds a target per child collection, recursively. Throws before anything runs when a
        /// child type cannot be deleted here, since deleting the parent alone would orphan it.
        /// </summary>
        private static void AddChildren(Target parent, Func<Type, SqlTableSchema?> resolveTable, HashSet<Type> path)
        {
            var parentSchema = parent.Table.ObjectSchema;
            if (!parentSchema.HasNucleusChildCollections)
                return;
            foreach (var property in parentSchema.NucleusChildCollectionProperties!)
            {
                var childType = property.ChildType ?? property.Type.GetGenericArguments()[0];
                var childTable = resolveTable(childType)
                    ?? throw new NotSupportedException(
                        $"{parentSchema.Name}.{property.Name} holds {childType.Name}, which this provider does not store, so its rows cannot be deleted with the parent.");
                var childSchema = childTable.ObjectSchema;
                if (!childSchema.IsDeletable)
                    throw new NotSupportedException(
                        $"{childSchema.FullName} is not [Deletable], so a {parentSchema.Name} cannot be deleted without orphaning it.");
                var parentProperty = childSchema.ParentObjectProperty
                    ?? throw new InvalidQueryException($"{childSchema.Name} is used as a child collection but has no [ParentObject] property.");
                var parentColumn = childTable.FindByPropertyName(parentProperty.Name)?.ColumnName
                    ?? throw new InvalidQueryException($"{childSchema.Name} has no column for its [ParentObject] property {parentProperty.Name}.");
                if (!path.Add(childType))
                    throw new InvalidQueryException($"{childSchema.Name} is its own ancestor through child collections.");
                var child = new Target(childTable, parent, parentColumn);
                parent.Children.Add(child);
                AddChildren(child, resolveTable, path);
                path.Remove(childType);
            }
        }

        /// <summary>Every target, root first.</summary>
        private static IEnumerable<Target> Targets(Target target)
        {
            yield return target;
            foreach (var child in target.Children)
                foreach (var descendant in Targets(child))
                    yield return descendant;
        }

        /// <summary>The condition selecting a target's rows; a child selects by its parent's.</summary>
        private string Filter(Target target)
        {
            string condition;
            if (target.Parent == null)
            {
                if (Where.IsEmpty)
                    throw new InvalidQueryException(
                        $"A delete of {target.Table.ObjectSchema.Name} has no condition and would remove every row.");
                condition = $"({Where.WhereClause()})";
            }
            else
            {
                var parentId = target.Parent.Table.IdentityColumn.ColumnName;
                condition = $"[{target.ParentColumn}] IN (SELECT [{parentId}] FROM {target.Parent.Table.TableName} WHERE {Filter(target.Parent)})";
            }
            return target.IsSoft ? $"{condition} AND {SoftDelete.LiveRowFilter}" : condition;
        }

        private string Statement(Target target) => target.IsSoft
            ? $"UPDATE {target.Table.TableName} WITH (ROWLOCK) SET {string.Join(",", target.SetParameters.Select(p => p.SQLSetCondition()))} WHERE {Filter(target)}"
            : $"DELETE FROM {target.Table.TableName} WHERE {Filter(target)}";

        /// <summary>Children of a target, deepest first, then the target itself.</summary>
        private void AppendChildrenFirst(StringBuilder sb, Target target)
        {
            foreach (var child in target.Children)
            {
                AppendChildrenFirst(sb, child);
                sb.Append(Statement(child)).Append(';').Append(Environment.NewLine);
            }
        }

        /// <summary>
        /// This chunk's batch only. Linked chunks are separate commands, so unlike the other
        /// queries this never appends them.
        /// </summary>
        public override string BuildQuery()
        {
            var sb = new StringBuilder();
            AppendChildrenFirst(sb, _root);
            sb.Append(Statement(_root)).Append(';').Append(Environment.NewLine);
            sb.Append("SELECT @@ROWCOUNT");
            return sb.ToString();
        }
    }
}
