using TFM.Nucleus.Abstractions.Interfaces;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// The read of a loaded [LinkedObject]: a load stores only the referenced entity's ID, and the
    /// first read of the property loads the entity through the loader the load bound, once,
    /// without making the object dirty. A save never loads a link to walk it, and an included
    /// reference is one linked query matched on the reference column.
    /// </summary>
    public class LinkedObjectReadTests
    {
        private sealed class CountingLoader(Func<Type, Guid, ICoreObject?> load) : ILinkedObjectLoader
        {
            private int _calls;
            public int Calls => Volatile.Read(ref _calls);

            public ICoreObject? LoadLinkedObject(Type type, Guid id)
            {
                Interlocked.Increment(ref _calls);
                return load(type, id);
            }
        }

        private static CmLinkTarget StoredTarget(Guid id) =>
            ObjectSchema<CmLinkTarget>.BuildObjectViaReflection(new Dictionary<string, object?> { ["ID"] = id, ["Name"] = "target" });

        /// <summary>A source as a load leaves it: stored, with its link held as the target's ID.</summary>
        private static CmLinkSource LoadedSource(Guid targetId, ILinkedObjectLoader loader)
        {
            var source = ObjectSchema<CmLinkSource>.BuildObjectViaReflection(
                new Dictionary<string, object?> { ["ID"] = Guid.NewGuid(), ["Name"] = "source", ["Target"] = targetId });
            ((ICoreObject)source).BindLinkedObjectLoader(loader);
            return source;
        }

        private static ILinkedObjectLoader Refusing() =>
            new CountingLoader((type, _) => throw new InvalidOperationException($"{type.Name} must not be loaded here."));

        [Fact]
        public void TheFirstRead_LoadsTheEntityThroughTheBoundLoader_AndTheNextReadRunsNoLoad()
        {
            var targetId = Guid.NewGuid();
            var loader = new CountingLoader((type, id) => type == typeof(CmLinkTarget) ? StoredTarget(id) : null);
            var source = LoadedSource(targetId, loader);

            var first = source.Target;
            var second = source.Target;

            Assert.NotNull(first);
            Assert.Equal(targetId, first.ID);
            Assert.Same(first, second);
            Assert.Equal(1, loader.Calls);
        }

        [Fact]
        public void TheRead_LeavesTheObjectUnchanged()
        {
            var targetId = Guid.NewGuid();
            var source = LoadedSource(targetId, new CountingLoader((_, id) => StoredTarget(id)));

            Assert.NotNull(source.Target);

            Assert.Empty(((ICoreObject)source).ChangedProperties);
            Assert.False(SaveGraph.IsChanged(source));
        }

        [Fact]
        public void AMissingRow_ReadsAsNull_AndIsLookedForAgainOnTheNextRead()
        {
            var loader = new CountingLoader((_, _) => null);
            var source = LoadedSource(Guid.NewGuid(), loader);

            Assert.Null(source.Target);
            Assert.Null(source.Target);
            Assert.Equal(2, loader.Calls);
        }

        [Fact]
        public async Task ReadsAtTheSameTime_LoadTheEntityOnce()
        {
            var targetId = Guid.NewGuid();
            var loader = new CountingLoader((_, id) =>
            {
                Thread.Sleep(20);
                return StoredTarget(id);
            });
            var source = LoadedSource(targetId, loader);

            var reads = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => source.Target)));

            Assert.Equal(1, loader.Calls);
            Assert.All(reads, read => Assert.Same(reads[0], read));
        }

        [Fact]
        public void ASaveWalk_DoesNotLoadALinkNobodyRead()
        {
            var source = LoadedSource(Guid.NewGuid(), Refusing());
            source.Name = "changed";

            var graph = SaveGraph.Walk([source]);

            Assert.Empty(graph.Links);
        }

        [Fact]
        public void ASaveWalk_WritesALinkThatWasReadAndChanged()
        {
            var source = LoadedSource(Guid.NewGuid(), new CountingLoader((_, id) => StoredTarget(id)));
            source.Target!.Name = "changed";

            var graph = SaveGraph.Walk([source]);

            Assert.Same(source.Target, Assert.Single(graph.Links));
        }

        [Fact]
        public void GetLinkedObjects_LeavesOutALinkNobodyRead()
        {
            IObjectSchema schema = ObjectSchema<CmLinkSource>.Get();
            var source = LoadedSource(Guid.NewGuid(), Refusing());

            Assert.Empty(schema.GetLinkedObjects(source));
        }

        [Fact]
        public void AStoredGuidUnderAPropertyThatIsNotALinkedObject_StillThrowsAsAWrongTypedValue()
        {
            var widget = ObjectSchema<TestWidget>.BuildObjectViaReflection(
                new Dictionary<string, object?> { ["ID"] = Guid.NewGuid(), ["Name"] = Guid.NewGuid() });

            Assert.Throws<InvalidCastException>(() => widget.Name);
        }

        // ---- Include ------------------------------------------------------------------------------

        private static Func<Type, TableSchema?> Tables(params Type[] types)
        {
            var tables = types.ToDictionary(t => t, t => new TableSchema(ObjectSchema.Get(t)));
            return t => tables.GetValueOrDefault(t);
        }

        [Fact]
        public void IncludeLinkedObject_AddsOneQueryMatchedOnTheReferenceColumn()
        {
            var tables = Tables(typeof(TestLinkingWidget), typeof(TestWidget));
            var query = new SelectQuery(SqlServerQueryRenderer.Instance, tables(typeof(TestLinkingWidget))!, tables);

            Assert.True(query.IncludeLinkedObject(nameof(TestLinkingWidget.Widget)));
            Assert.True(query.IncludeLinkedObject(nameof(TestLinkingWidget.Widget)));

            var reference = Assert.Single(query.LinkedQueries);
            Assert.Equal(nameof(TestLinkingWidget.Widget), reference.LinkedObjectProperty);
            Assert.Equal("widget_ref", reference.ParentKeyColumn);
            Assert.Equal("ID", reference.LinkedColumn);
        }

        [Fact]
        public void IncludeLinkedObject_IsFalseForAPropertyThatIsNotALinkedObject()
        {
            var tables = Tables(typeof(TestLinkingWidget), typeof(TestWidget));
            var query = new SelectQuery(SqlServerQueryRenderer.Instance, tables(typeof(TestLinkingWidget))!, tables);

            Assert.False(query.IncludeLinkedObject(nameof(TestLinkingWidget.Name)));
            Assert.Empty(query.LinkedQueries);
        }

        [Fact]
        public void IncludeLinkedObject_ThrowsWhenTheProviderDoesNotStoreTheReferencedType()
        {
            var tables = Tables(typeof(TestLinkingWidget));
            var query = new SelectQuery(SqlServerQueryRenderer.Instance, tables(typeof(TestLinkingWidget))!, tables);

            var ex = Assert.Throws<InvalidQueryException>(() => query.IncludeLinkedObject(nameof(TestLinkingWidget.Widget)));
            Assert.Contains(nameof(TestWidget), ex.Message);
        }

        [Fact]
        public void AReferenceSelector_NamesTheProperty()
        {
            Assert.Equal(nameof(CmLinkSource.Target), ChildCollectionSelector.PropertyName<CmLinkSource>(s => s.Target));
        }
    }
}
