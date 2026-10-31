using TFM.Nucleus.Abstractions.Interfaces;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// A load can leave a child collection out. The collection then says so: reading it throws
    /// rather than answering as if the parent had no children, while adding still works.
    /// </summary>
    public class ExcludeChildCollectionTests
    {
        private static T Loaded<T>(Guid id) where T : CoreObject =>
            ObjectSchema<T>.BuildObjectViaReflection(new Dictionary<string, object?> { ["ID"] = id });

        private static NucleusChildCollection<TestDeletableChild, TestDeletableParent> UnloadedChildren(out TestDeletableParent parent)
        {
            parent = Loaded<TestDeletableParent>(Guid.NewGuid());
            var children = parent.Children;
            ((INucleusChildCollection)children).MarkUnloaded();
            return children;
        }

        // ---- The unloaded collection -----------------------------------------------------------

        [Fact]
        public void Unloaded_ReadsAndRemovalsThrow()
        {
            var children = UnloadedChildren(out _);
            var child = Loaded<TestDeletableChild>(Guid.NewGuid());

            Assert.False(children.IsLoaded);
            Assert.Throws<InvalidOperationException>(() => children.Count);
            Assert.Throws<InvalidOperationException>(() => children.ToList());
            Assert.Throws<InvalidOperationException>(() => children[0]);
            Assert.Throws<InvalidOperationException>(() => children.Contains(child));
            Assert.Throws<InvalidOperationException>(() => children.Remove(child));
            Assert.Throws<InvalidOperationException>(() => children.Clear());
        }

        [Fact]
        public void Unloaded_AddStillWorks_AndSavePathsSeeTheAddedChildren()
        {
            var children = UnloadedChildren(out _);
            var added = new TestDeletableChild { Sequence = 1 };

            children.Add(added);

            Assert.Same(added, Assert.Single(((INucleusChildCollection)children).ChildObjects));
        }

        [Fact]
        public void AttachLoaded_MarksLoaded_BindsCleanly_AndMergesById()
        {
            var children = UnloadedChildren(out var parent);
            var addedThenSaved = Loaded<TestDeletableChild>(Guid.NewGuid());
            children.Add(addedThenSaved);
            addedThenSaved.ClearChanges();
            var stored = Loaded<TestDeletableChild>(Guid.NewGuid());
            var storedCopyOfAdded = Loaded<TestDeletableChild>(addedThenSaved.ID);

            ((INucleusChildCollection)children).AttachLoaded([stored, storedCopyOfAdded]);

            Assert.True(children.IsLoaded);
            Assert.Equal(2, children.Count);
            Assert.Contains(addedThenSaved, children);
            Assert.Contains(stored, children);
            Assert.Same(parent, stored.Parent);
            Assert.False(stored.HasChanges);
        }

        // ---- Naming the collection -------------------------------------------------------------

        [Fact]
        public void Selector_NamesTheProperty()
        {
            Assert.Equal("Children", ChildCollectionSelector.PropertyName<TestDeletableParent>(p => p.Children));
        }

        [Fact]
        public void Selector_NotAPropertyRead_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                ChildCollectionSelector.PropertyName<TestDeletableParent>(p => new TestDeletableParent().Children));
        }

        // ---- Through the manager and the adaptor -----------------------------------------------

        private static (ObjectDataManager Manager, FakeDataProvider Provider) ManagerWithParent(Guid id)
        {
            var provider = new FakeDataProvider();
            provider.SetupRows<TestDeletableParent>(new Dictionary<string, object?> { ["ID"] = id, ["Name"] = "p" });
            return (new ObjectDataManager(provider, new ObjectSchemaProvider()), provider);
        }

        [Fact]
        public void AdaptorExclude_RemovesTheChildQuery_AndLeavesTheCollectionUnloaded()
        {
            var (manager, provider) = ManagerWithParent(Guid.NewGuid());

            var parent = Assert.Single(manager.Get<TestDeletableParent>().Exclude(p => p.Children).ToList());

            Assert.False(parent.Children.IsLoaded);
            var query = Assert.IsType<FakeQuery>(Assert.Single(provider.CreatedQueries));
            Assert.Equal(["Children"], query.ExcludedCollections);
        }

        [Fact]
        public async Task ExcludeSurvivesWhere_InEitherOrder()
        {
            var (manager, provider) = ManagerWithParent(Guid.NewGuid());

            var before = await manager.Get<TestDeletableParent>().Exclude(p => p.Children).Where(p => p.Name == "p").ToListAsync();
            var after = await manager.Get<TestDeletableParent>().Where(p => p.Name == "p").Exclude(p => p.Children).ToListAsync();

            Assert.False(Assert.Single(before).Children.IsLoaded);
            Assert.False(Assert.Single(after).Children.IsLoaded);
            Assert.All(provider.CreatedQueries.Cast<FakeQuery>(), q => Assert.Equal(["Children"], q.ExcludedCollections));
        }

        [Fact]
        public void LoadWithoutExclude_LeavesTheCollectionLoaded()
        {
            var (manager, _) = ManagerWithParent(Guid.NewGuid());

            var parent = Assert.Single(manager.Get<TestDeletableParent>().ToList());

            Assert.True(parent.Children.IsLoaded);
        }

        [Fact]
        public void Exclude_UnknownCollection_Throws()
        {
            var (manager, _) = ManagerWithParent(Guid.NewGuid());

            Assert.Throws<ArgumentException>(() => manager.Load<TestDeletableParent>(System.Linq.Expressions.Expression.Constant(true), ["Nope"]));
        }

        // ---- The SQL -------------------------------------------------------------------------------

        [Fact]
        public void SelectQuery_ExcludeChildCollection_DropsItsSelect()
        {
            var parent = new SelectQuery(SqlServerQueryRenderer.Instance, new SqlTableSchema(ObjectSchema.Get<TestDeletableWidget>(), null!));
            var child = new SelectQuery(SqlServerQueryRenderer.Instance, new SqlTableSchema(ObjectSchema.Get<TestDeletableChild>(), null!)) { ChildCollectionProperty = "Children" };
            parent.AddChildQuery(child, "Parent");

            Assert.True(parent.ExcludeChildCollection("Children"));
            Assert.False(parent.ExcludeChildCollection("Children"));
            Assert.DoesNotContain("TestDeletableChild", parent.BuildQuery());
        }
    }
}
