namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// Verifies the schema object builder: the IL-emitted path and the reflection fallback must
    /// produce identical, correctly-initialized objects (loaded state, not-new, empty pending edits).
    /// </summary>
    public class ObjectMaterializerTests
    {
        private static Dictionary<string, object?> WidgetProperties(Guid id) => new()
        {
            ["ID"] = id,
            ["Name"] = "Widget A",
            ["Quantity"] = 7,
        };

        [Fact]
        public void EmitBuilder_MaterializesLoadedObject()
        {
            var id = Guid.NewGuid();
            var builder = ObjectSchema<TestWidget>.CreateEmitObjectBuilder();

            var widget = builder(WidgetProperties(id));

            Assert.Equal(id, widget.ID);
            Assert.Equal("Widget A", widget.Name);
            Assert.Equal(7, widget.Quantity);
            Assert.False(widget.IsNew);
            Assert.False(widget.HasChanges);
        }

        [Fact]
        public void ReflectionBuilder_MaterializesLoadedObject()
        {
            var id = Guid.NewGuid();

            var widget = ObjectSchema<TestWidget>.BuildObjectViaReflection(WidgetProperties(id));

            Assert.Equal(id, widget.ID);
            Assert.Equal("Widget A", widget.Name);
            Assert.Equal(7, widget.Quantity);
            Assert.False(widget.IsNew);
            Assert.False(widget.HasChanges);
        }

        [Fact]
        public void EmitAndReflectionBuilders_ProduceEquivalentObjects()
        {
            var id = Guid.NewGuid();
            var emitBuilder = ObjectSchema<TestWidget>.CreateEmitObjectBuilder();

            var fromEmit = emitBuilder(WidgetProperties(id));
            var fromReflection = ObjectSchema<TestWidget>.BuildObjectViaReflection(WidgetProperties(id));

            Assert.Equal(fromEmit.ID, fromReflection.ID);
            Assert.Equal(fromEmit.Name, fromReflection.Name);
            Assert.Equal(fromEmit.Quantity, fromReflection.Quantity);
            Assert.Equal(fromEmit.IsNew, fromReflection.IsNew);
            Assert.Equal(fromEmit.HasChanges, fromReflection.HasChanges);
        }

        [Fact]
        public void MaterializedObject_TracksSubsequentEdits()
        {
            var builder = ObjectSchema<TestWidget>.CreateEmitObjectBuilder();
            var widget = builder(WidgetProperties(Guid.NewGuid()));

            widget.Name = "Renamed";

            Assert.True(widget.HasChanges);
            Assert.Equal("Renamed", widget.Name);
            Assert.Equal(7, widget.Quantity);
        }

        [Fact]
        public void SchemaBuildObject_UsesConfiguredBuilder()
        {
            var id = Guid.NewGuid();
            var schema = ObjectSchema<TestWidget>.Get();

            var widget = schema.BuildObject(WidgetProperties(id));

            Assert.Equal(id, widget.ID);
            Assert.Equal("Widget A", widget.Name);
        }
    }
}
