using System.Linq.Expressions;
using Microsoft.Extensions.Logging.Abstractions;

namespace TFM.Nucleus.Tests
{
    [Deletable]
    public class TestDeletableWidget : CoreObject<TestDeletableWidget>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    [Deletable(DeleteBehavior.Soft)]
    public class TestSoftWidget : CoreObject<TestSoftWidget>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    [Deletable]
    public class TestDeletableParent : CoreObject<TestDeletableParent>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        public NucleusChildCollection<TestDeletableChild, TestDeletableParent> Children => GetChildCollection<TestDeletableChild, TestDeletableParent>();
    }

    [Deletable]
    public class TestDeletableChild : CoreChildObject<TestDeletableChild, TestDeletableParent>
    {
        [DatabaseColumn]
        public int Sequence { get => GetValue<int>(); set => SetValue(value); }
    }

    public class TestParentWithUndeletableChild : CoreObject<TestParentWithUndeletableChild>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    [Deletable]
    public class TestParentOfUndeletable : CoreObject<TestParentOfUndeletable>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        public NucleusChildCollection<TestUndeletableChild, TestParentOfUndeletable> Children => GetChildCollection<TestUndeletableChild, TestParentOfUndeletable>();
    }

    public class TestUndeletableChild : CoreChildObject<TestUndeletableChild, TestParentOfUndeletable>
    {
        [DatabaseColumn]
        public int Sequence { get => GetValue<int>(); set => SetValue(value); }
    }

    public class DeleteTests
    {
        private static (ObjectDataManager Manager, FakeDataProvider Provider) NewManager()
        {
            var provider = new FakeDataProvider();
            return (new ObjectDataManager(provider, new ObjectSchemaProvider(), null), provider);
        }

        private static T Loaded<T>(Guid id) where T : CoreObject =>
            ObjectSchema<T>.BuildObjectViaReflection(new Dictionary<string, object?> { ["ID"] = id });

        [Fact]
        public async Task DeleteAsync_UndecoratedType_Throws()
        {
            var (manager, _) = NewManager();
            var widget = Loaded<TestWidget>(Guid.NewGuid());

            await Assert.ThrowsAsync<NotSupportedException>(() => manager.DeleteAsync(widget));
        }

        [Fact]
        public async Task DeleteAsync_DeletableObject_ExecutesTransactionalDelete()
        {
            var (manager, provider) = NewManager();
            var widget = Loaded<TestDeletableWidget>(Guid.NewGuid());

            await manager.DeleteAsync(widget);

            var context = Assert.Single(provider.Contexts);
            Assert.True(context.HasTransaction);
            var query = Assert.IsType<FakeQuery>(Assert.Single(context.ExecutedQueries));
            Assert.Equal(FakeQueryKind.Delete, query.Kind);
            Assert.Same(widget, Assert.Single(query.Objects));
        }

        [Fact]
        public async Task DeleteAsync_NewObject_IsSkippedSilently()
        {
            var (manager, provider) = NewManager();
            var widget = new TestDeletableWidget { Name = "unsaved" };

            await manager.DeleteAsync(widget);

            Assert.Empty(provider.Contexts);
        }

        [Fact]
        public async Task DeleteAsync_ParentWithLoadedChildren_LeavesTheCascadeToTheProvider()
        {
            // The provider's delete removes child rows by parent ID, loaded or not, so the
            // manager sends only the parent. Collecting loaded children orphaned unloaded ones.
            var (manager, provider) = NewManager();
            var parent = Loaded<TestDeletableParent>(Guid.NewGuid());
            var child = Loaded<TestDeletableChild>(Guid.NewGuid());
            parent.Children.Add(child);

            await manager.DeleteAsync(parent);

            var context = Assert.Single(provider.Contexts);
            var query = Assert.IsType<FakeQuery>(Assert.Single(context.ExecutedQueries));
            Assert.Equal(typeof(TestDeletableParent), query.EntityType);
            Assert.Same(parent, Assert.Single(query.Objects));
        }

        // ---- Delete by predicate: the manager --------------------------------------------------

        [Fact]
        public async Task DeleteWhereAsync_RunsInOneTransactionAndReportsTheCount()
        {
            var (manager, provider) = NewManager();
            provider.RowsAffectedByDeleteWhere = 3;

            var affected = await manager.DeleteWhereAsync<TestDeletableWidget>(w => w.Name == "old");

            Assert.Equal(3, affected);
            var context = Assert.Single(provider.Contexts);
            Assert.True(context.HasTransaction);
            var (entityType, _) = Assert.Single(context.DeletedWhere);
            Assert.Equal(typeof(TestDeletableWidget), entityType);
        }

        [Fact]
        public void DeleteWhere_PredicateMatchingEveryRow_IsRefused()
        {
            var (manager, provider) = NewManager();
            var everything = true;

            Assert.Throws<ArgumentException>(() => manager.DeleteWhere<TestDeletableWidget>(w => true));
            Assert.Throws<ArgumentException>(() => manager.DeleteWhere<TestDeletableWidget>(w => w.Name == "a" || everything));
            Assert.Empty(provider.Contexts);
        }

        [Fact]
        public async Task DeleteWhereAsync_PredicateMatchingNothing_MakesNoRoundTrip()
        {
            var (manager, provider) = NewManager();

            var affected = await manager.DeleteWhereAsync<TestDeletableWidget>(w => false);

            Assert.Equal(0, affected);
            Assert.Empty(provider.Contexts);
        }

        // ---- Delete by predicate: the SQL ------------------------------------------------------

        private static Func<Type, SqlTableSchema?> Tables(params Type[] types)
        {
            var tables = types.ToDictionary(t => t, t => new SqlTableSchema(ObjectSchema.Get(t), null!));
            return t => tables.GetValueOrDefault(t);
        }

        private static string DeleteWhereSql<T>(Expression<Func<T, bool>> predicate, Func<Type, SqlTableSchema?> tables)
        {
            var query = new DeleteWhereQuery(tables(typeof(T))!, tables);
            ExpressionParser.ParseExpressionToQuery(query, predicate.Body);
            return SqlSnapshot.Normalize(query.BuildQuery());
        }

        [Fact]
        public void DeleteWhere_Hard_Golden()
        {
            var sql = DeleteWhereSql<TestDeletableWidget>(w => w.Name == "old", Tables(typeof(TestDeletableWidget)));

            Assert.Equal(
                "DELETE FROM tbl_TFM_Nucleus_Tests_TestDeletableWidget WHERE ([Name]=@p0); SELECT @@ROWCOUNT",
                sql);
        }

        [Fact]
        public void DeleteWhere_Soft_FlagsOnlyLiveRows()
        {
            var sql = DeleteWhereSql<TestSoftWidget>(w => w.Name == "old", Tables(typeof(TestSoftWidget)));

            Assert.Equal(
                "UPDATE tbl_TFM_Nucleus_Tests_TestSoftWidget WITH (ROWLOCK) SET [IsDeleted] = @p0,[DeletedAt] = SYSDATETIMEOFFSET() " +
                "WHERE ([Name]=@p1) AND [IsDeleted] = 0; SELECT @@ROWCOUNT",
                sql);
        }

        [Fact]
        public void DeleteWhere_ParentWithChildren_DeletesChildrenFirstThroughTheParentCondition()
        {
            var sql = DeleteWhereSql<TestDeletableParent>(
                p => p.Name == "old" || p.Name == "older",
                Tables(typeof(TestDeletableParent), typeof(TestDeletableChild)));

            Assert.Equal(
                "DELETE FROM tbl_TFM_Nucleus_Tests_TestDeletableChild WHERE [Parent] IN " +
                "(SELECT [ID] FROM tbl_TFM_Nucleus_Tests_TestDeletableParent WHERE ((([Name]=@p0) OR ([Name]=@p1)))); " +
                "DELETE FROM tbl_TFM_Nucleus_Tests_TestDeletableParent WHERE ((([Name]=@p0) OR ([Name]=@p1))); " +
                "SELECT @@ROWCOUNT",
                sql);
        }

        [Fact]
        public void DeleteWhere_ParentWithChildren_DeclaresEachParameterOnce()
        {
            var tables = Tables(typeof(TestDeletableParent), typeof(TestDeletableChild));
            var query = new DeleteWhereQuery(tables(typeof(TestDeletableParent))!, tables);
            ExpressionParser.ParseExpressionToQuery(query, ((Expression<Func<TestDeletableParent, bool>>)(p => p.Name == "old")).Body);

            Assert.Single(query.Parameters);
        }

        [Fact]
        public void DeleteById_CascadesByParentId()
        {
            var tables = Tables(typeof(TestDeletableParent), typeof(TestDeletableChild));

            var query = DeleteWhereQuery.ForIds(tables(typeof(TestDeletableParent))!, tables, [Guid.NewGuid(), Guid.NewGuid()])!;
            var sql = SqlSnapshot.Normalize(query.BuildQuery());

            Assert.Equal(
                "DELETE FROM tbl_TFM_Nucleus_Tests_TestDeletableChild WHERE [Parent] IN " +
                "(SELECT [ID] FROM tbl_TFM_Nucleus_Tests_TestDeletableParent WHERE ([ID] IN (@p0_0,@p0_1))); " +
                "DELETE FROM tbl_TFM_Nucleus_Tests_TestDeletableParent WHERE ([ID] IN (@p0_0,@p0_1)); " +
                "SELECT @@ROWCOUNT",
                sql);
        }

        [Fact]
        public void DeleteById_ManyIds_ChunksIntoLinkedCommands()
        {
            var tables = Tables(typeof(TestDeletableWidget));
            var ids = Enumerable.Range(0, DeleteWhereQuery.IdsPerStatement + 1).Select(_ => Guid.NewGuid());

            var query = DeleteWhereQuery.ForIds(tables(typeof(TestDeletableWidget))!, tables, ids)!;

            Assert.Single(query.LinkedQueries);
            Assert.DoesNotContain("tbl_", query.BuildQuery().Split("SELECT @@ROWCOUNT")[1]);
        }

        [Fact]
        public void DeleteById_NoIds_IsNull()
        {
            var tables = Tables(typeof(TestDeletableWidget));

            Assert.Null(DeleteWhereQuery.ForIds(tables(typeof(TestDeletableWidget))!, tables, []));
        }

        [Fact]
        public void DeleteWhere_EmptyCondition_RefusesToRender()
        {
            var tables = Tables(typeof(TestDeletableWidget));
            var query = new DeleteWhereQuery(tables(typeof(TestDeletableWidget))!, tables);

            Assert.Throws<InvalidQueryException>(() => query.BuildQuery());
        }

        [Fact]
        public void DeleteWhere_UndeletableChildType_ThrowsBeforeRendering()
        {
            var tables = Tables(typeof(TestParentOfUndeletable), typeof(TestUndeletableChild));

            Assert.Throws<NotSupportedException>(() => new DeleteWhereQuery(tables(typeof(TestParentOfUndeletable))!, tables));
        }

        [Fact]
        public void DeleteWhere_ChildTypeOnAnotherProvider_ThrowsBeforeRendering()
        {
            var tables = Tables(typeof(TestDeletableParent));

            Assert.Throws<NotSupportedException>(() => new DeleteWhereQuery(tables(typeof(TestDeletableParent))!, tables));
        }

        [Fact]
        public void HardDelete_Golden()
        {
            var schema = new SqlTableSchema(ObjectSchema.Get<TestDeletableWidget>(), null!);
            var widget = Loaded<TestDeletableWidget>(Guid.NewGuid());

            var query = DeleteQuery.Create(schema, [widget])!;
            var sql = SqlSnapshot.Normalize(query.BuildQuery());

            Assert.Equal("DELETE FROM tbl_TFM_Nucleus_Tests_TestDeletableWidget WHERE [ID]=@p0", sql);
        }

        [Fact]
        public void SoftDelete_RendersFlagUpdate()
        {
            var schema = new SqlTableSchema(ObjectSchema.Get<TestSoftWidget>(), null!);
            var widget = Loaded<TestSoftWidget>(Guid.NewGuid());

            var query = DeleteQuery.Create(schema, [widget])!;
            var sql = SqlSnapshot.Normalize(query.BuildQuery());

            Assert.Equal(
                "UPDATE tbl_TFM_Nucleus_Tests_TestSoftWidget WITH (ROWLOCK) SET [IsDeleted] = @p0,[DeletedAt] = SYSDATETIMEOFFSET() WHERE [ID]=@p1",
                sql);
        }

        [Fact]
        public void Select_SoftDeletableType_FiltersDeletedRows()
        {
            var schema = new SqlTableSchema(ObjectSchema.Get<TestSoftWidget>(), null!);
            var query = new SelectQuery(schema);

            var sql = SqlSnapshot.Normalize(query.BuildQuery());

            Assert.EndsWith("WHERE [IsDeleted] = 0", sql);
        }

        [Fact]
        public void Select_SoftDeletableType_IncludeDeletedSkipsFilter()
        {
            var schema = new SqlTableSchema(ObjectSchema.Get<TestSoftWidget>(), null!);
            var query = new SelectQuery(schema) { IncludeDeleted = true };

            var sql = SqlSnapshot.Normalize(query.BuildQuery());

            Assert.DoesNotContain("[IsDeleted]", sql);
        }

        [Fact]
        public void HardDeletableType_SelectHasNoFilter()
        {
            var schema = new SqlTableSchema(ObjectSchema.Get<TestDeletableWidget>(), null!);
            var query = new SelectQuery(schema);

            var sql = SqlSnapshot.Normalize(query.BuildQuery());

            Assert.DoesNotContain("[IsDeleted]", sql);
        }
    }
}
