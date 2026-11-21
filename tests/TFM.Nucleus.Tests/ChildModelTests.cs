using System.ComponentModel;
using TFM.Nucleus.Abstractions.Interfaces;

namespace TFM.Nucleus.Tests
{
    // ---- Delete declarations: each pair isolates one rule ----------------------------------------

    /// <summary>Declared on the child type: deletable directly and through the owner.</summary>
    public class CmTypeOwner : CoreObject<CmTypeOwner>
    {
        [DatabaseColumn] public string? Name { get => GetValue<string>(); set => SetValue(value); }
        public NucleusChildCollection<CmTypeLine, CmTypeOwner> Lines => GetChildCollection<CmTypeLine, CmTypeOwner>();
    }

    [Deletable]
    public class CmTypeLine : CoreChildObject<CmTypeLine, CmTypeOwner>
    {
        [DatabaseColumn] public int Sequence { get => GetValue<int>(); set => SetValue(value); }
    }

    /// <summary>Declared on the collection property only: deletable through the owner, not on its own.</summary>
    public class CmPropertyOwner : CoreObject<CmPropertyOwner>
    {
        [DatabaseColumn] public string? Name { get => GetValue<string>(); set => SetValue(value); }
        [Deletable]
        public NucleusChildCollection<CmPropertyLine, CmPropertyOwner> Lines => GetChildCollection<CmPropertyLine, CmPropertyOwner>();
    }

    public class CmPropertyLine : CoreChildObject<CmPropertyLine, CmPropertyOwner>
    {
        [DatabaseColumn] public int Sequence { get => GetValue<int>(); set => SetValue(value); }
    }

    /// <summary>Declared soft on the owner: the whole owned graph is deletable, soft, through it.</summary>
    [Deletable(DeleteBehavior.Soft)]
    public class CmSoftOwner : CoreObject<CmSoftOwner>
    {
        [DatabaseColumn] public string? Name { get => GetValue<string>(); set => SetValue(value); }
        public NucleusChildCollection<CmSoftLine, CmSoftOwner> Lines => GetChildCollection<CmSoftLine, CmSoftOwner>();
    }

    public class CmSoftLine : CoreChildObject<CmSoftLine, CmSoftOwner>
    {
        [DatabaseColumn] public int Sequence { get => GetValue<int>(); set => SetValue(value); }
        public NucleusChildCollection<CmSoftLeaf, CmSoftLine> Leaves => GetChildCollection<CmSoftLeaf, CmSoftLine>();
    }

    public class CmSoftLeaf : CoreChildObject<CmSoftLeaf, CmSoftLine>
    {
        [DatabaseColumn] public int Sequence { get => GetValue<int>(); set => SetValue(value); }
    }

    /// <summary>The owner forbids its own deletion; the collection still deletes its removals.</summary>
    [Deletable(false)]
    public class CmLockedOwner : CoreObject<CmLockedOwner>
    {
        [DatabaseColumn] public string? Name { get => GetValue<string>(); set => SetValue(value); }
        [Deletable]
        public NucleusChildCollection<CmLockedLine, CmLockedOwner> Lines => GetChildCollection<CmLockedLine, CmLockedOwner>();
    }

    public class CmLockedLine : CoreChildObject<CmLockedLine, CmLockedOwner>
    {
        [DatabaseColumn] public int Sequence { get => GetValue<int>(); set => SetValue(value); }
    }

    /// <summary>The child type forbids deletion under an owner that would delete it: a schema error.</summary>
    [Deletable]
    public class CmConflictOwner : CoreObject<CmConflictOwner>
    {
        [DatabaseColumn] public string? Name { get => GetValue<string>(); set => SetValue(value); }
        public NucleusChildCollection<CmConflictLine, CmConflictOwner> Lines => GetChildCollection<CmConflictLine, CmConflictOwner>();
    }

    [Deletable(false)]
    public class CmConflictLine : CoreChildObject<CmConflictLine, CmConflictOwner>
    {
        [DatabaseColumn] public int Sequence { get => GetValue<int>(); set => SetValue(value); }
    }

    /// <summary>The child type's own behaviour beats the owner's.</summary>
    [Deletable(DeleteBehavior.Soft)]
    public class CmMixedOwner : CoreObject<CmMixedOwner>
    {
        [DatabaseColumn] public string? Name { get => GetValue<string>(); set => SetValue(value); }
        public NucleusChildCollection<CmMixedLine, CmMixedOwner> Lines => GetChildCollection<CmMixedLine, CmMixedOwner>();
    }

    [Deletable(DeleteBehavior.Hard)]
    public class CmMixedLine : CoreChildObject<CmMixedLine, CmMixedOwner>
    {
        [DatabaseColumn] public int Sequence { get => GetValue<int>(); set => SetValue(value); }
    }

    /// <summary>A behaviour declared on the collection applies to its undeclared children.</summary>
    public class CmPropertySoftOwner : CoreObject<CmPropertySoftOwner>
    {
        [DatabaseColumn] public string? Name { get => GetValue<string>(); set => SetValue(value); }
        [Deletable(DeleteBehavior.Soft)]
        public NucleusChildCollection<CmPropertySoftLine, CmPropertySoftOwner> Lines => GetChildCollection<CmPropertySoftLine, CmPropertySoftOwner>();
    }

    public class CmPropertySoftLine : CoreChildObject<CmPropertySoftLine, CmPropertySoftOwner>
    {
        [DatabaseColumn] public int Sequence { get => GetValue<int>(); set => SetValue(value); }
    }

    /// <summary>Nothing declared: nothing is deletable.</summary>
    public class CmPlainOwner : CoreObject<CmPlainOwner>
    {
        [DatabaseColumn] public string? Name { get => GetValue<string>(); set => SetValue(value); }
        public NucleusChildCollection<CmPlainLine, CmPlainOwner> Lines => GetChildCollection<CmPlainLine, CmPlainOwner>();
    }

    public class CmPlainLine : CoreChildObject<CmPlainLine, CmPlainOwner>
    {
        [DatabaseColumn] public int Total { get => GetValue<int>(); set => SetValue(value); }

        public override void Validate()
        {
            if (Total < 0)
                throw new InvalidOperationException("A line total cannot be negative.");
            base.Validate();
        }
    }

    /// <summary>[Deletable(false)] on a collection has no meaning and is refused.</summary>
    public class CmFalsePropertyOwner : CoreObject<CmFalsePropertyOwner>
    {
        [DatabaseColumn] public string? Name { get => GetValue<string>(); set => SetValue(value); }
        [Deletable(false)]
        public NucleusChildCollection<CmFalsePropertyLine, CmFalsePropertyOwner> Lines => GetChildCollection<CmFalsePropertyLine, CmFalsePropertyOwner>();
    }

    public class CmFalsePropertyLine : CoreChildObject<CmFalsePropertyLine, CmFalsePropertyOwner>
    {
        [DatabaseColumn] public int Sequence { get => GetValue<int>(); set => SetValue(value); }
    }

    // ---- Schema-build refusals -----------------------------------------------------------------

    public class CmDeletableColumn : CoreObject<CmDeletableColumn>
    {
        [Deletable]
        [DatabaseColumn] public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    public class CmTwoCollections : CoreObject<CmTwoCollections>
    {
        [DatabaseColumn] public string? Name { get => GetValue<string>(); set => SetValue(value); }
        public NucleusChildCollection<CmTwinLine, CmTwoCollections> First => GetChildCollection<CmTwinLine, CmTwoCollections>();
        public NucleusChildCollection<CmTwinLine, CmTwoCollections> Second => GetChildCollection<CmTwinLine, CmTwoCollections>();
    }

    public class CmTwinLine : CoreChildObject<CmTwinLine, CmTwoCollections>
    {
        [DatabaseColumn] public int Sequence { get => GetValue<int>(); set => SetValue(value); }
    }

    [Cacheable]
    public class CmCachedOwner : CoreObject<CmCachedOwner>
    {
        [DatabaseColumn] public string? Name { get => GetValue<string>(); set => SetValue(value); }
        [RetrieveOnDemand]
        public NucleusChildCollection<CmCachedLine, CmCachedOwner> Lines => GetChildCollection<CmCachedLine, CmCachedOwner>();
    }

    public class CmCachedLine : CoreChildObject<CmCachedLine, CmCachedOwner>
    {
        [DatabaseColumn] public int Sequence { get => GetValue<int>(); set => SetValue(value); }
    }

    // ---- forceSave links -----------------------------------------------------------------------

    public class CmLinkTarget : CoreObject<CmLinkTarget>
    {
        [DatabaseColumn] public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [LinkedObject(forceSave: true)]
        [DatabaseColumn]
        public CmLinkSource? Back { get => GetValue<CmLinkSource>(); set => SetValue(value); }
    }

    public class CmLinkSource : CoreObject<CmLinkSource>
    {
        [DatabaseColumn] public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [LinkedObject(forceSave: true)]
        [DatabaseColumn]
        public CmLinkTarget? Target { get => GetValue<CmLinkTarget>(); set => SetValue(value); }
    }

    public class ChildModelTests
    {
        private static T Loaded<T>(Guid id) where T : CoreObject =>
            ObjectSchema<T>.BuildObjectViaReflection(new Dictionary<string, object?> { ["ID"] = id });

        /// <summary>Puts children into a collection as loaded: stored, bound, unchanged.</summary>
        private static void AttachStored(INucleusChildCollection collection, params ICoreObject[] children) =>
            collection.AttachLoaded(children);

        private static Func<Type, TableSchema?> Tables(params Type[] types)
        {
            var tables = types.ToDictionary(t => t, t => new TableSchema(ObjectSchema.Get(t)));
            return t => tables.GetValueOrDefault(t);
        }

        // ---- The policy ---------------------------------------------------------------------------

        [Fact]
        public void Declared_OnChildType_DeletableDirectlyAndThroughOwner()
        {
            IObjectSchema line = ObjectSchema<CmTypeLine>.Get();

            Assert.True(line.IsDeletable);
            Assert.True(line.IsDeletableThroughParent);
            Assert.True(new CmTypeOwner().Lines.DeletesRemovedChildren);
            Assert.False(((IObjectSchema)ObjectSchema<CmTypeOwner>.Get()).IsDeletable);
        }

        [Fact]
        public void Declared_OnProperty_DeletableThroughOwnerOnly()
        {
            IObjectSchema line = ObjectSchema<CmPropertyLine>.Get();

            Assert.False(line.IsDeletable);
            Assert.True(line.IsDeletableThroughParent);
            Assert.Equal(DeleteBehavior.Hard, line.EffectiveDeleteBehavior);
            Assert.True(new CmPropertyOwner().Lines.DeletesRemovedChildren);
        }

        [Fact]
        public void Declared_OnOwner_ReachesTheWholeGraph_WithItsBehaviour()
        {
            IObjectSchema line = ObjectSchema<CmSoftLine>.Get();
            IObjectSchema leaf = ObjectSchema<CmSoftLeaf>.Get();

            Assert.False(line.IsDeletable);
            Assert.True(line.IsDeletableThroughParent);
            Assert.Equal(DeleteBehavior.Soft, line.EffectiveDeleteBehavior);
            Assert.True(leaf.IsDeletableThroughParent);
            Assert.Equal(DeleteBehavior.Soft, leaf.EffectiveDeleteBehavior);
            Assert.True(new CmSoftOwner().Lines.DeletesRemovedChildren);
        }

        [Fact]
        public void UndeclaredChild_UnderSoftOwner_GetsTheSoftDeleteColumns()
        {
            var tables = Tables(typeof(CmSoftLine), typeof(CmSoftLeaf));
            var table = tables(typeof(CmSoftLine))!;

            Assert.True(table.IsSoftDeletable);
            // The soft-delete columns are system columns the dialect writes; a read filters on them.
            var select = new SelectQuery(SqlServerQueryRenderer.Instance, table, tables).BuildQuery();
            Assert.Contains(SoftDelete.IsDeletedColumn, select);
        }

        [Fact]
        public void OwnerForbidsItsOwnDeletion_PropertyStillDeletesRemovals()
        {
            Assert.False(((IObjectSchema)ObjectSchema<CmLockedOwner>.Get()).IsDeletable);
            Assert.True(((IObjectSchema)ObjectSchema<CmLockedLine>.Get()).IsDeletableThroughParent);
            Assert.True(new CmLockedOwner().Lines.DeletesRemovedChildren);
        }

        [Fact]
        public void ChildForbidsDeletion_UnderAnEnablingOwner_FailsTheSchema()
        {
            IObjectSchema line = ObjectSchema<CmConflictLine>.Get();

            var ex = Assert.Throws<ObjectSchemaException>(() => line.IsDeletableThroughParent);
            Assert.Contains("[Deletable(false)]", ex.Message);
            Assert.Contains("CmConflictOwner", ex.Message);
            Assert.Throws<ObjectSchemaException>(() => line.Check());
            Assert.Throws<ObjectSchemaException>(() => new TableSchema(line));
        }

        [Fact]
        public void ChildTypesOwnBehaviour_BeatsTheOwners()
        {
            IObjectSchema line = ObjectSchema<CmMixedLine>.Get();

            Assert.Equal(DeleteBehavior.Hard, line.EffectiveDeleteBehavior);
            Assert.False(new TableSchema(line).IsSoftDeletable);
        }

        [Fact]
        public void BehaviourDeclaredOnProperty_AppliesToUndeclaredChildren()
        {
            IObjectSchema line = ObjectSchema<CmPropertySoftLine>.Get();

            Assert.Equal(DeleteBehavior.Soft, line.EffectiveDeleteBehavior);
            Assert.False(line.IsDeletable);
        }

        [Fact]
        public void NothingDeclared_NothingDeletable()
        {
            IObjectSchema line = ObjectSchema<CmPlainLine>.Get();

            Assert.False(line.IsDeletable);
            Assert.False(line.IsDeletableThroughParent);
            Assert.False(new CmPlainOwner().Lines.DeletesRemovedChildren);
        }

        [Fact]
        public void DeletableFalse_OnAProperty_IsRefused()
        {
            Assert.Throws<ObjectSchemaException>(() => ((IObjectSchema)ObjectSchema<CmFalsePropertyLine>.Get()).IsDeletableThroughParent);
        }

        [Fact]
        public void Deletable_OnAColumn_IsRefused() =>
            Assert.Throws<ObjectSchemaException>(() => ObjectSchema.Get(typeof(CmDeletableColumn)));

        [Fact]
        public void TwoCollectionsOfOneChildType_AreRefused()
        {
            var ex = Assert.Throws<ObjectSchemaException>(() => ObjectSchema.Get(typeof(CmTwoCollections)));
            Assert.Contains("CmTwinLine", ex.ToString());
        }

        [Fact]
        public void LazyCollection_OnACacheableType_IsRefused()
        {
            var ex = Assert.Throws<ObjectSchemaException>(() => ObjectSchema.Get(typeof(CmCachedOwner)));
            Assert.Contains("[RetrieveOnDemand]", ex.ToString());
        }

        [Fact]
        public async Task ChildDeletableOnlyThroughOwner_RefusesADirectDelete()
        {
            var manager = new ObjectDataManager(new FakeDataProvider(), new ObjectSchemaProvider());
            var owner = Loaded<CmPropertyOwner>(Guid.NewGuid());
            var line = Loaded<CmPropertyLine>(Guid.NewGuid());
            AttachStored(owner.Lines, line);

            await Assert.ThrowsAsync<NotSupportedException>(() => manager.DeleteAsync(line));
        }

        [Fact]
        public void DeletingASoftOwner_CascadesSoftToUndeclaredChildrenAndLeaves()
        {
            var tables = Tables(typeof(CmSoftOwner), typeof(CmSoftLine), typeof(CmSoftLeaf));
            var query = DeleteWhereQuery.ForIds(SqlServerQueryRenderer.Instance, tables(typeof(CmSoftOwner))!, tables, [Guid.NewGuid()])!;

            Assert.Equal(2, query.TargetsChildrenFirst().Count());
            Assert.All(query.TargetsChildrenFirst(), target => Assert.True(target.IsSoft));
            Assert.True(query.Root.IsSoft);
        }

        [Fact]
        public void RemovedChildren_DeleteThroughTheirOwner_EvenWhenNotDeletableDirectly()
        {
            var tables = Tables(typeof(CmPropertyLine));

            Assert.Throws<NotSupportedException>(() =>
                DeleteWhereQuery.ForIds(SqlServerQueryRenderer.Instance, tables(typeof(CmPropertyLine))!, tables, [Guid.NewGuid()]));
            var query = DeleteWhereQuery.ForRemovedChildren(SqlServerQueryRenderer.Instance, tables(typeof(CmPropertyLine))!, tables, [Guid.NewGuid()]);
            Assert.NotNull(query);
            Assert.StartsWith("DELETE FROM", SqlSnapshot.Normalize(query!.BuildQuery()));
        }

        // ---- Removing from a collection ----------------------------------------------------------

        [Fact]
        public void RemovingAStoredChild_WhenNotEnabled_ThrowsAndChangesNothing()
        {
            var owner = Loaded<CmPlainOwner>(Guid.NewGuid());
            var line = Loaded<CmPlainLine>(Guid.NewGuid());
            AttachStored(owner.Lines, line);

            var ex = Assert.Throws<InvalidOperationException>(() => owner.Lines.Remove(line));
            Assert.Contains("[Deletable]", ex.Message);
            Assert.Throws<InvalidOperationException>(() => owner.Lines.RemoveAt(0));
            Assert.Throws<InvalidOperationException>(() => owner.Lines[0] = new CmPlainLine());
            Assert.Same(line, Assert.Single(owner.Lines));
            Assert.Empty(((INucleusChildCollection)owner.Lines).RemovedObjects);
        }

        [Fact]
        public void Clear_IsAllOrNothing()
        {
            var owner = Loaded<CmPlainOwner>(Guid.NewGuid());
            var stored = Loaded<CmPlainLine>(Guid.NewGuid());
            AttachStored(owner.Lines, stored);
            owner.Lines.Add(new CmPlainLine());

            Assert.Throws<InvalidOperationException>(() => owner.Lines.Clear());
            Assert.Equal(2, owner.Lines.Count);
        }

        [Fact]
        public void RemovingANewChild_IsAlwaysAllowed_AndNotRecorded()
        {
            var owner = new CmPlainOwner();
            var line = new CmPlainLine();
            owner.Lines.Add(line);

            Assert.True(owner.Lines.Remove(line));
            Assert.Empty(((INucleusChildCollection)owner.Lines).RemovedObjects);
        }

        [Fact]
        public void RemovingAStoredChild_WhenEnabled_RecordsItForTheSave()
        {
            var owner = Loaded<CmPropertyOwner>(Guid.NewGuid());
            var line = Loaded<CmPropertyLine>(Guid.NewGuid());
            AttachStored(owner.Lines, line);

            Assert.True(owner.Lines.Remove(line));

            Assert.Same(line, Assert.Single(((INucleusChildCollection)owner.Lines).RemovedObjects));
            Assert.Same(line, Assert.Single(SaveGraph.Walk([owner]).Removed));
        }

        [Fact]
        public void RemovedThenAddedBack_IsKept_AndStaysBound()
        {
            var owner = Loaded<CmPropertyOwner>(Guid.NewGuid());
            var stored = Loaded<CmPropertyLine>(Guid.NewGuid());
            AttachStored(owner.Lines, stored);
            var fresh = new CmPropertyLine();
            owner.Lines.Add(fresh);

            owner.Lines.Remove(stored);
            owner.Lines.Remove(fresh);
            owner.Lines.Add(stored);
            owner.Lines.Add(fresh);

            Assert.Empty(((INucleusChildCollection)owner.Lines).RemovedObjects);
            Assert.Same(owner, (object)stored.Parent);
            Assert.Same(owner, (object)fresh.Parent);
            Assert.Equal(2, owner.Lines.Count);
        }

        [Fact]
        public void AcknowledgeRemovals_RunsWhenTheOwnerCommits()
        {
            var owner = Loaded<CmPropertyOwner>(Guid.NewGuid());
            var line = Loaded<CmPropertyLine>(Guid.NewGuid());
            AttachStored(owner.Lines, line);
            owner.Lines.Remove(line);

            ((ICoreObject)owner).CommitChanges();

            Assert.Empty(((INucleusChildCollection)owner.Lines).RemovedObjects);
        }

        [Fact]
        public void ChangingAChild_RaisesPropertyChangedForTheCollectionProperty()
        {
            var owner = new CmPlainOwner();
            var names = new List<string?>();
            ((INotifyPropertyChanged)owner).PropertyChanged += (_, e) => names.Add(e.PropertyName);
            var line = new CmPlainLine();

            owner.Lines.Add(line);
            line.Total = 3;

            Assert.NotEmpty(names);
            Assert.All(names, name => Assert.Equal(nameof(CmPlainOwner.Lines), name));
        }

        // ---- Validation reaches the children -----------------------------------------------------

        [Fact]
        public void Validate_RunsTheRulesOfNewAndChangedChildren()
        {
            var owner = new CmPlainOwner { Name = "o" };
            owner.Lines.Add(new CmPlainLine { Total = -1 });

            var ex = Assert.Throws<InvalidOperationException>(owner.Validate);
            Assert.Contains("negative", ex.Message);
        }

        [Fact]
        public void Validate_SkipsUnchangedLoadedChildren()
        {
            var owner = Loaded<CmPlainOwner>(Guid.NewGuid());
            var line = ObjectSchema<CmPlainLine>.BuildObjectViaReflection(new Dictionary<string, object?> { ["ID"] = Guid.NewGuid(), ["Total"] = -5 });
            AttachStored(owner.Lines, line);

            owner.Validate();
        }

        // ---- The save graph ------------------------------------------------------------------------

        [Fact]
        public void SaveGraph_WritesChangedForceSaveLinks_SkipsCleanOnes_AndEndsOnACycle()
        {
            var source = new CmLinkSource { Name = "source" };
            // Stored with a link back to the root, so the walk meets a cycle.
            var target = ObjectSchema<CmLinkTarget>.BuildObjectViaReflection(
                new Dictionary<string, object?> { ["ID"] = Guid.NewGuid(), ["Back"] = source });
            source.Target = target;

            Assert.Empty(SaveGraph.Walk([source]).Links);

            target.Name = "changed";
            var graph = SaveGraph.Walk([source]);

            Assert.Same(target, Assert.Single(graph.Links));
            Assert.Same(source, Assert.Single(graph.Roots));
            Assert.Contains(target, graph.ChangedDependents);
        }

        [Fact]
        public void Commit_CommitsTheForceSaveLinksTheSaveWrote()
        {
            var source = new CmLinkSource { Name = "source" };
            var target = Loaded<CmLinkTarget>(Guid.NewGuid());
            source.Target = target;
            target.Name = "changed";

            ((ICoreObject)source).CommitChanges();

            Assert.False(target.HasChanges);
            Assert.Equal("changed", target.Name);
        }
    }

    // ---- Lazy collections ------------------------------------------------------------------------

    public class CmLazyOwner : CoreObject<CmLazyOwner>
    {
        [DatabaseColumn] public string? Name { get => GetValue<string>(); set => SetValue(value); }
        [RetrieveOnDemand]
        public NucleusChildCollection<CmLazyLine, CmLazyOwner> Lines => GetChildCollection<CmLazyLine, CmLazyOwner>();
    }

    public class CmLazyLine : CoreChildObject<CmLazyLine, CmLazyOwner>
    {
        [DatabaseColumn] public int Sequence { get => GetValue<int>(); set => SetValue(value); }
    }

    /// <summary>
    /// Lazy loads go through the ambient runtime, which is process-wide, so these share a
    /// collection with the other tests that install one.
    /// </summary>
    [Collection(AmbientRuntime.Name)]
    public class LazyChildCollectionTests
    {
        private static (ObjectDataManager Manager, FakeDataProvider Provider, Guid OwnerId) Install()
        {
            var provider = new FakeDataProvider();
            var manager = new ObjectDataManager(provider, new ObjectSchemaProvider());
            NucleusRuntime.Current = manager;
            var ownerId = Guid.NewGuid();
            provider.SetupRows<CmLazyOwner>(new Dictionary<string, object?> { ["ID"] = ownerId, ["Name"] = "owner" });
            provider.SetupRows<CmLazyLine>(
                new Dictionary<string, object?> { ["ID"] = Guid.NewGuid(), ["Sequence"] = 1 },
                new Dictionary<string, object?> { ["ID"] = Guid.NewGuid(), ["Sequence"] = 2 });
            return (manager, provider, ownerId);
        }

        private static int ChildQueries(FakeDataProvider provider) =>
            provider.CreatedQueries.OfType<FakeQuery>().Count(q => q.EntityType == typeof(CmLazyLine));

        [Fact]
        public void LoadedParent_LeavesTheLazyCollectionUnloaded_UntilItIsRead()
        {
            var (manager, provider, ownerId) = Install();

            var owner = manager.LoadByID<CmLazyOwner>(ownerId)!;

            Assert.False(owner.Lines.IsLoaded);
            Assert.Equal(0, ChildQueries(provider));

            Assert.Equal(2, owner.Lines.Count);

            Assert.True(owner.Lines.IsLoaded);
            Assert.Equal(1, ChildQueries(provider));
            Assert.All(owner.Lines, line => Assert.Same(owner, (object)line.Parent));
        }

        [Fact]
        public void ConcurrentFirstReads_LoadOnce()
        {
            var (manager, provider, ownerId) = Install();
            var owner = manager.LoadByID<CmLazyOwner>(ownerId)!;

            Parallel.For(0, 16, _ => Assert.Equal(2, owner.Lines.Count));

            Assert.Equal(1, ChildQueries(provider));
        }

        [Fact]
        public void Include_AsksTheQueryForTheCollection()
        {
            var (manager, provider, _) = Install();

            _ = manager.Get<CmLazyOwner>().Include(o => o.Lines).Where(o => o.Name == "owner").ToList();

            var query = provider.CreatedQueries.OfType<FakeQuery>().First(q => q.EntityType == typeof(CmLazyOwner));
            Assert.Equal(["Lines"], query.IncludedCollections);
        }

        [Fact]
        public void ExplicitlyExcluded_StillThrows()
        {
            var (manager, _, ownerId) = Install();

            var owner = manager.LoadByID<CmLazyOwner>(ownerId, ["Lines"])!;

            Assert.Throws<InvalidOperationException>(() => owner.Lines.Count);
        }

        [Fact]
        public void AddingWithoutLoading_SavesWithTheParent()
        {
            var (manager, _, ownerId) = Install();
            var owner = manager.LoadByID<CmLazyOwner>(ownerId)!;
            var added = new CmLazyLine { Sequence = 3 };

            owner.Lines.Add(added);

            Assert.Same(added, Assert.Single(((INucleusChildCollection)owner.Lines).ChildObjects));
            Assert.False(owner.Lines.IsLoaded);
        }

        [Fact]
        public void IncludeAndExclude_OfOneCollection_IsRefused()
        {
            var (manager, _, _) = Install();

            Assert.Throws<ArgumentException>(() => manager.Load<CmLazyOwner>(null, new LoadOptions { Exclude = ["Lines"], Include = ["Lines"] }));
        }
    }

    /// <summary>Tests that set <see cref="NucleusRuntime.Current"/> run one at a time.</summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class AmbientRuntime
    {
        public const string Name = "Ambient runtime";
    }
}
