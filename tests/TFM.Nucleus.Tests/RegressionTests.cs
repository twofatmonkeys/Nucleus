using System.ComponentModel;
using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// Regression cover for the defects listed in docs/code-review-findings.md. Each test names the
    /// behaviour that was wrong, so a reintroduction fails here rather than in production.
    /// </summary>
    public class RegressionTests
    {
        private static IPropertySchema Property<T>(string name) where T : CoreObject =>
            ObjectSchema.Get<T>().GetProperty(name);

        // ---- GetDBValue and nullable value types ----------------------------------------------

        [Fact]
        public void GetDBValue_AcceptsNullableIntValue()
        {
            var widget = new NullableWidget { OptionalQuantity = 42 };

            var value = Property<NullableWidget>(nameof(NullableWidget.OptionalQuantity)).GetDBValue(widget);

            Assert.Equal(42, value);
        }

        [Fact]
        public void GetDBValue_AcceptsNullableDecimalValue()
        {
            var widget = new NullableWidget { OptionalPrice = 19.95m };

            var value = Property<NullableWidget>(nameof(NullableWidget.OptionalPrice)).GetDBValue(widget);

            Assert.Equal(19.95m, value);
        }

        [Fact]
        public void GetDBValue_AcceptsNullableEnumValue()
        {
            var widget = new NullableWidget { OptionalKind = WidgetKind.Premium };

            var value = Property<NullableWidget>(nameof(NullableWidget.OptionalKind)).GetDBValue(widget);

            Assert.Equal(WidgetKind.Premium, value);
        }

        [Fact]
        public void BindDataSet_WidensIntegerIntoNullableEnum()
        {
            // The reader hands back the column's int, and a boxed int cannot be unboxed straight
            // into WidgetKind? the way it can into WidgetKind, so the bind has to widen it.
            var dataSet = DataSet.Initialize("tbl_NullableWidget", ["ID", "Kind", "OptionalKind"]);
            dataSet.FillNext([Guid.NewGuid(), 2, 1]);

            var widget = Assert.Single(NullableWidget.BindDataSet(dataSet));

            Assert.Equal(WidgetKind.Premium, widget.Kind);
            Assert.Equal(WidgetKind.Standard, widget.OptionalKind);
        }

        [Fact]
        public void BindDataSet_LeavesNullableEnumNull()
        {
            var dataSet = DataSet.Initialize("tbl_NullableWidget", ["ID", "Kind", "OptionalKind"]);
            dataSet.FillNext([Guid.NewGuid(), 0, DBNull.Value]);

            var widget = Assert.Single(NullableWidget.BindDataSet(dataSet));

            Assert.Null(widget.OptionalKind);
        }

        [Fact]
        public void GetDBValue_UnsetNullableIsDbNull()
        {
            var widget = new NullableWidget();

            var value = Property<NullableWidget>(nameof(NullableWidget.OptionalQuantity)).GetDBValue(widget);

            Assert.Equal(DBNull.Value, value);
        }

        // ---- Non-public setters ----------------------------------------------------------------

        [Fact]
        public void SetValue_WritesThroughNonPublicSetter()
        {
            var widget = new NullableWidget();

            Property<NullableWidget>(nameof(NullableWidget.RestrictedName)).SetValue(widget, "restricted");

            Assert.Equal("restricted", widget.RestrictedName);
        }

        // ---- Schema type tests that were always false ------------------------------------------

        [Fact]
        public void IsDerivedClass_RecognisesEntityTypes()
        {
            Assert.True(CoreObject.IsDerivedClass(typeof(TestWidget)));
            Assert.True(CoreObject.IsDerivedClass<NullableWidget>());
            Assert.False(CoreObject.IsDerivedClass(typeof(string)));
            Assert.False(CoreObject.IsDerivedClass(typeof(ICoreObject)));
        }

        [Fact]
        public void ObjectSchema_OfLinkedEntityProperty_IsResolved()
        {
            // LinkedObjectSchemas used to be an array of nulls, because the underlying test asked
            // whether a System.Type instance implemented ICoreObject.
            var schema = ObjectSchema.Get<LinkedRoot>();

            Assert.NotEmpty(schema.LinkedObjectSchemas!);
            Assert.All(schema.LinkedObjectSchemas!, Assert.NotNull);
        }

        // ---- Shadowed members -------------------------------------------------------------------

        [Fact]
        public void Schema_BuildsForTypeThatShadowsABaseMember()
        {
            var schema = ObjectSchema.Get<ShadowDerived>();

            Assert.NotNull(schema);
            Assert.NotNull(schema.GetProperty(nameof(ShadowDerived.Label)));
        }

        // ---- Change tracking ---------------------------------------------------------------------

        [Fact]
        public void Changes_IsUsableOnAMaterialisedObject()
        {
            var widget = Materialise("loaded");

            // Both of these dereferenced a null ChangeCollection before.
            Assert.False(widget.DataChanged);
            widget.ClearChanges();
        }

        [Fact]
        public void SetValue_AssigningTheCommittedValueBackIsNotAChange()
        {
            var widget = Materialise("original");

            widget.Name = "changed";
            Assert.True(widget.HasChanges);

            widget.Name = "original";
            Assert.False(widget.HasChanges);
        }

        [Fact]
        public void SetValue_AssigningTheSameValueRaisesNoPropertyChanged()
        {
            var widget = Materialise("original");

            var raised = 0;
            ((INotifyPropertyChanged)widget).PropertyChanged += (_, _) => raised++;

            widget.Name = "original";

            Assert.Equal(0, raised);
        }

        [Fact]
        public void DataChanged_AgreesWithHasChanges()
        {
            var widget = Materialise("original");

            Assert.Equal(widget.HasChanges, widget.DataChanged);

            widget.Name = "changed";

            Assert.Equal(widget.HasChanges, widget.DataChanged);
            Assert.True(widget.DataChanged);
        }

        private static TestWidget Materialise(string name) =>
            ObjectSchema<TestWidget>.BuildObjectViaReflection(new Dictionary<string, object?>
            {
                ["ID"] = Guid.NewGuid(),
                ["Name"] = name,
            });

        // ---- Expression rewriting ---------------------------------------------------------------

        [Fact]
        public void ChangeTypeOfT_RewritesNodeTypesTheOldSwitchMissed()
        {
            Expression<Func<SourceShape, bool>> predicate =
                s => !s.Active && s.Quantity > 2 && (s.Name ?? "x") == "abc";

            var compiled = predicate.ChangeTypeOfT<SourceShape, TargetShape>().Compile();

            Assert.True(compiled(new TargetShape { Active = false, Quantity = 3, Name = "abc" }));
            Assert.False(compiled(new TargetShape { Active = true, Quantity = 3, Name = "abc" }));
            Assert.False(compiled(new TargetShape { Active = false, Quantity = 1, Name = "abc" }));
            Assert.False(compiled(new TargetShape { Active = false, Quantity = 3, Name = "zzz" }));
        }

        [Fact]
        public void ChangeTypeOfT_RewritesInstanceMethodCalls()
        {
            Expression<Func<SourceShape, bool>> predicate = s => s.Name!.StartsWith("ab");

            var compiled = predicate.ChangeTypeOfT<SourceShape, TargetShape>().Compile();

            Assert.True(compiled(new TargetShape { Name = "abc" }));
            Assert.False(compiled(new TargetShape { Name = "zzz" }));
        }

        // ---- Registration idempotency ------------------------------------------------------------

        [Fact]
        public void AddNucleus_CalledTwice_RegistersOneSetOfServices()
        {
            var services = new ServiceCollection();
            services.AddLogging();

            services.AddNucleus(o => o.AppName = "test");
            services.AddNucleus(o => o.AppName = "test");

            Assert.Single(services, d => d.ServiceType == typeof(IObjectSchemaProvider));
            Assert.Single(services, d => d.ServiceType == typeof(INucleusServiceProvider));
        }

        [Fact]
        public void AddNucleusLogging_CalledTwice_RegistersOneProvider()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNucleus(o => o.AppName = "test");

            services.AddNucleusLogging();
            services.AddNucleusLogging();

            var providers = services
                .Where(d => d.ServiceType == typeof(ILoggerProvider) && d.ImplementationFactory is not null)
                .ToList();

            Assert.Single(providers);

            // Building proves the descriptor is well formed: TryAddEnumerable rejects a factory it
            // cannot tell apart from the service type, which would throw here.
            using var built = services.BuildServiceProvider();
            Assert.NotEmpty(built.GetServices<ILoggerProvider>());
        }

        // ---- Type converter -----------------------------------------------------------------------

        [Fact]
        public void TypeConverter_ConvertsEntityToItsId()
        {
            var widget = new TestWidget();
            var converter = TypeDescriptor.GetConverter(typeof(TestWidget));

            Assert.True(converter.CanConvertTo(typeof(Guid)));
            Assert.Equal(widget.ID, converter.ConvertTo(widget, typeof(Guid)));
        }

        [Fact]
        public void TypeConverter_DoesNotClaimToConvertFromGuid()
        {
            var converter = TypeDescriptor.GetConverter(typeof(TestWidget));

            // It used to advertise this conversion and then throw for every input.
            Assert.False(converter.CanConvertFrom(typeof(Guid)));
        }
    }

    /// <summary>Entity with a linked object, used to prove LinkedObjectSchemas resolves.</summary>
    public class LinkedRoot : CoreObject<LinkedRoot>
    {
        [LinkedObject]
        [DatabaseColumn]
        public TestWidget? Widget
        {
            get => GetValue<TestWidget>();
            set => SetValue(value);
        }
    }
}
