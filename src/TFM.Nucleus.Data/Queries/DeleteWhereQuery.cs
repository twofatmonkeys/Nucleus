using System;
using System.Collections.Generic;
using System.Linq;
using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.Data.Queries
{
    /// <summary>
    /// Deletes every row of a [Deletable] type that matches a WHERE, without loading any of them,
    /// and cascades to the rows of its child collections by parent ID. The batch deletes the
    /// deepest children first (<see cref="TargetsChildrenFirst"/>), so each child statement still
    /// finds the parent rows it selects by, and ends with the root statement and a result that
    /// tells the caller how many rows of the root type were affected.
    /// <para>
    /// Each type keeps its own delete behaviour: a hard type is deleted, a soft type is flagged.
    /// A child selects by the same condition its parent's statement uses, soft filter included,
    /// so it is removed with exactly the parents this batch removes.
    /// </para>
    /// <para>
    /// The batch runs as one command through the provider's dedicated delete path, never the
    /// generic one: that path regroups queries by table and detaches parents, which would break
    /// the subqueries and the order. Further chunks of an ID list hang off
    /// <see cref="Query.LinkedQueries"/> and run as their own commands in the same context.
    /// </para>
    /// </summary>
    public class DeleteWhereQuery : Query, IDeleteQuery
    {
        /// <summary>IDs per statement when deleting a list of objects, well under the parameter limit.</summary>
        public const int IdsPerStatement = 1000;

        /// <summary>The table being deleted from, with the cascade below it.</summary>
        public DeleteTarget Root { get; }

        /// <param name="renderer">The provider's renderer.</param>
        /// <param name="tableSchema">The table of the type being deleted.</param>
        /// <param name="resolveTable">
        /// Finds the table of a child type, or null when this provider does not own it.
        /// </param>
        public DeleteWhereQuery(IQueryRenderer renderer, IDataTableSchema tableSchema, Func<Type, IDataTableSchema?> resolveTable)
            : this(renderer, tableSchema, resolveTable, throughOwner: false)
        {
        }

        /// <param name="renderer">The provider's renderer.</param>
        /// <param name="tableSchema">The table of the type being deleted.</param>
        /// <param name="resolveTable">Finds the table of a child type, or null when this provider does not own it.</param>
        /// <param name="throughOwner">
        /// True for rows removed from their owner's child collection, which need only be
        /// deletable through their owner; false for a direct delete, which needs the type's own [Deletable].
        /// </param>
        private DeleteWhereQuery(IQueryRenderer renderer, IDataTableSchema tableSchema, Func<Type, IDataTableSchema?> resolveTable, bool throughOwner) : base(renderer)
        {
            ArgumentNullException.ThrowIfNull(tableSchema);
            ArgumentNullException.ThrowIfNull(resolveTable);
            if (throughOwner ? !tableSchema.ObjectSchema.IsDeletableThroughParent : !tableSchema.ObjectSchema.IsDeletable)
                throw new NotSupportedException(throughOwner
                    ? $"Type {tableSchema.ObjectSchema.FullName} is not deletable through its owner; declare [Deletable] on it, its collection or its owner."
                    : $"Type {tableSchema.ObjectSchema.FullName} is not [Deletable]; deletion is opt-in.");
            Type = QueryKind.Delete;
            TableName = tableSchema.TableName;
            TableSchema = tableSchema;
            ObjectType = tableSchema.ObjectSchema.Type;
            Root = new DeleteTarget(tableSchema, null, null);
            AddChildren(Root, resolveTable, [tableSchema.ObjectSchema.Type]);
            foreach (var target in Targets(Root))
                SetParameters.AddRange(target.SetParameters);
        }

        /// <summary>
        /// Deletes the given IDs of one type with their child rows: one query per
        /// <see cref="IdsPerStatement"/> IDs, the rest linked. Null when there are no IDs.
        /// </summary>
        public static DeleteWhereQuery? ForIds(IQueryRenderer renderer, IDataTableSchema tableSchema, Func<Type, IDataTableSchema?> resolveTable, IEnumerable<Guid> ids) =>
            ForIds(renderer, tableSchema, resolveTable, ids, throughOwner: false);

        /// <summary>
        /// Deletes children removed from their owner's collection, by ID, with their own child
        /// rows. The type need only be deletable through its owner. Null when there are no IDs.
        /// </summary>
        public static DeleteWhereQuery? ForRemovedChildren(IQueryRenderer renderer, IDataTableSchema tableSchema, Func<Type, IDataTableSchema?> resolveTable, IEnumerable<Guid> ids) =>
            ForIds(renderer, tableSchema, resolveTable, ids, throughOwner: true);

        private static DeleteWhereQuery? ForIds(IQueryRenderer renderer, IDataTableSchema tableSchema, Func<Type, IDataTableSchema?> resolveTable, IEnumerable<Guid> ids, bool throughOwner)
        {
            var identity = tableSchema.IdentityColumn
                ?? throw new InvalidQueryException($"Table {tableSchema.TableName} has no identity column.");
            DeleteWhereQuery? root = null;
            foreach (var chunk in ids.Distinct().Chunk(IdsPerStatement))
            {
                var query = new DeleteWhereQuery(renderer, tableSchema, resolveTable, throughOwner);
                query.Where.And(new Parameter(identity.ColumnName, chunk.ToList(), Operator.In));
                if (root == null)
                    root = query;
                else
                    root.LinkedQueries.Add(query);
            }
            return root;
        }

        /// <summary>
        /// Adds a target per child collection, recursively. Throws before anything runs when a
        /// child type cannot be deleted through its owner, since deleting the parent alone would
        /// orphan it. Schema build refuses such a declaration first, so this is a backstop.
        /// </summary>
        private static void AddChildren(DeleteTarget parent, Func<Type, IDataTableSchema?> resolveTable, HashSet<Type> path)
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
                if (!childSchema.IsDeletableThroughParent)
                    throw new NotSupportedException(
                        $"{childSchema.FullName} is not deletable through its owner, so a {parentSchema.Name} cannot be deleted without orphaning it.");
                var parentProperty = childSchema.ParentObjectProperty
                    ?? throw new InvalidQueryException($"{childSchema.Name} is used as a child collection but has no [ParentObject] property.");
                var parentColumn = childTable.FindByPropertyName(parentProperty.Name)?.ColumnName
                    ?? throw new InvalidQueryException($"{childSchema.Name} has no column for its [ParentObject] property {parentProperty.Name}.");
                if (!path.Add(childType))
                    throw new InvalidQueryException($"{childSchema.Name} is its own ancestor through child collections.");
                var child = new DeleteTarget(childTable, parent, parentColumn);
                parent.Children.Add(child);
                AddChildren(child, resolveTable, path);
                path.Remove(childType);
            }
        }

        /// <summary>Every target, root first.</summary>
        private static IEnumerable<DeleteTarget> Targets(DeleteTarget target)
        {
            yield return target;
            foreach (var child in target.Children)
                foreach (var descendant in Targets(child))
                    yield return descendant;
        }

        /// <summary>
        /// The child targets in the order their statements run: each child's own children first,
        /// then the child, so the deepest rows go first. The root is not included; it runs last.
        /// </summary>
        public IEnumerable<DeleteTarget> TargetsChildrenFirst() => ChildrenFirst(Root);

        private static IEnumerable<DeleteTarget> ChildrenFirst(DeleteTarget target)
        {
            foreach (var child in target.Children)
            {
                foreach (var descendant in ChildrenFirst(child))
                    yield return descendant;
                yield return child;
            }
        }

        /// <summary>Throws when the delete has no condition and would remove every row.</summary>
        public void EnsureHasCondition()
        {
            if (Where.IsEmpty)
                throw new InvalidQueryException(
                    $"A delete of {Root.Table.ObjectSchema.Name} has no condition and would remove every row.");
        }

        /// <summary>
        /// This chunk's batch only. Linked chunks are separate commands, so unlike the other
        /// queries this never appends them.
        /// </summary>
        public override string BuildQuery() => Renderer.DeleteWhere(this);
    }

    /// <summary>One table in a delete cascade, with the column that links it to its parent.</summary>
    public sealed class DeleteTarget
    {
        internal DeleteTarget(IDataTableSchema table, DeleteTarget? parent, string? parentColumn)
        {
            Table = table;
            Parent = parent;
            ParentColumn = parentColumn;
            SetParameters = SoftDelete.Applies(table) ? SoftDelete.NewSetParameters() : [];
        }

        public IDataTableSchema Table { get; }

        /// <summary>The target whose rows this one's rows belong to; null for the root.</summary>
        public DeleteTarget? Parent { get; }

        /// <summary>The column of this table holding the parent's ID; null for the root.</summary>
        public string? ParentColumn { get; }

        public List<DeleteTarget> Children { get; } = [];

        /// <summary>The SET terms of a soft delete; empty for a hard delete.</summary>
        public Parameter[] SetParameters { get; }

        public bool IsSoft => SetParameters.Length > 0;
    }
}
