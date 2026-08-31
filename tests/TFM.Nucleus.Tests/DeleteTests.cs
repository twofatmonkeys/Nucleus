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
        public async Task DeleteAsync_CascadesToDeletableChildren()
        {
            var (manager, provider) = NewManager();
            var parent = Loaded<TestDeletableParent>(Guid.NewGuid());
            var child = Loaded<TestDeletableChild>(Guid.NewGuid());
            parent.Children.Add(child);

            await manager.DeleteAsync(parent);

            var context = Assert.Single(provider.Contexts);
            Assert.Equal(2, context.ExecutedQueries.Count);
            var kinds = context.ExecutedQueries.Cast<FakeQuery>().Select(q => q.EntityType).ToList();
            Assert.Contains(typeof(TestDeletableParent), kinds);
            Assert.Contains(typeof(TestDeletableChild), kinds);
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
