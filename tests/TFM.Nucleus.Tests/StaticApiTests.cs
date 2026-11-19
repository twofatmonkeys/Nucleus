namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// The CoreObject static convenience API must be thin forwarders over the ambient runtime.
    /// </summary>
    [Collection(AmbientRuntime.Name)]
    public class StaticApiTests
    {
        private static FakeDataProvider InstallRuntime()
        {
            var provider = new FakeDataProvider();
            NucleusRuntime.Current = new ObjectDataManager(provider, new ObjectSchemaProvider());
            return provider;
        }

        [Fact]
        public void LoadByID_ForwardsPredicateToProvider()
        {
            var provider = InstallRuntime();
            var id = Guid.NewGuid();
            provider.SetupRows<TestWidget>(new Dictionary<string, object?> { ["ID"] = id, ["Name"] = "static" });

            var loaded = CoreObject.LoadByID<TestWidget>(id);

            Assert.NotNull(loaded);
            Assert.Equal("static", loaded!.Name);
            var query = Assert.IsType<FakeQuery>(provider.CreatedQueries.Single());
            var parameter = Assert.Single(query.WhereParameters);
            Assert.Equal("ID", parameter.Name);
            Assert.Equal(id, parameter.Value);
        }

        [Fact]
        public void LoadByID_EmptyGuid_ReturnsNullWithoutQuerying()
        {
            var provider = InstallRuntime();

            Assert.Null(CoreObject.LoadByID<TestWidget>(Guid.Empty));
            Assert.Empty(provider.CreatedQueries);
        }

        [Fact]
        public async Task DeleteWhereAsync_ForwardsToRuntime()
        {
            var provider = InstallRuntime();
            provider.RowsAffectedByDeleteWhere = 2;

            var affected = await CoreObject.DeleteWhereAsync<TestDeletableWidget>(w => w.Name == "static");

            Assert.Equal(2, affected);
            var (entityType, _) = Assert.Single(Assert.Single(provider.Contexts).DeletedWhere);
            Assert.Equal(typeof(TestDeletableWidget), entityType);
        }

        [Fact]
        public void LoadByID_WithExclusion_LeavesTheCollectionUnloaded()
        {
            var provider = InstallRuntime();
            var id = Guid.NewGuid();
            provider.SetupRows<TestDeletableParent>(new Dictionary<string, object?> { ["ID"] = id, ["Name"] = "static" });

            var parent = CoreObject.LoadByID<TestDeletableParent>(id, p => p.Children);

            Assert.NotNull(parent);
            Assert.False(parent!.Children.IsLoaded);
            Assert.Equal(["Children"], Assert.IsType<FakeQuery>(Assert.Single(provider.CreatedQueries)).ExcludedCollections);
        }

        [Fact]
        public void LoadAll_ReturnsEveryRow()
        {
            var provider = InstallRuntime();
            provider.SetupRows<TestWidget>(
                new Dictionary<string, object?> { ["ID"] = Guid.NewGuid(), ["Name"] = "one" },
                new Dictionary<string, object?> { ["ID"] = Guid.NewGuid(), ["Name"] = "two" });

            var all = CoreObject.LoadAll<TestWidget>();

            Assert.Equal(2, all.Count);
        }

        [Fact]
        public void InstanceSave_RoutesThroughRuntime()
        {
            var provider = InstallRuntime();
            var widget = new TestWidget { Name = "instance-save" };

            widget.Save();

            var context = Assert.Single(provider.Contexts);
            var query = Assert.IsType<FakeQuery>(Assert.Single(context.ExecutedQueries));
            Assert.Same(widget, Assert.Single(query.Objects));
            Assert.False(widget.IsNew);
        }

        [Fact]
        public async Task InstanceDeleteAsync_RoutesThroughRuntime()
        {
            var provider = InstallRuntime();
            var item = ObjectSchema<TestDeletableWidget>.BuildObjectViaReflection(
                new Dictionary<string, object?> { ["ID"] = Guid.NewGuid() });

            await item.DeleteAsync();

            var context = Assert.Single(provider.Contexts);
            var query = Assert.IsType<FakeQuery>(Assert.Single(context.ExecutedQueries));
            Assert.Equal(FakeQueryKind.Delete, query.Kind);
        }
    }
}
