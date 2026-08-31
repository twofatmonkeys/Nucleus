namespace TFM.Nucleus.Tests
{
    public class TestInvoice : CoreObject<TestInvoice>
    {
        [DatabaseColumn]
        public string? Reference
        {
            get => GetValue<string>();
            set => SetValue(value);
        }

        [LinkedObject]
        public TestWidget? PrimaryWidget
        {
            get => GetValue<TestWidget>();
            set => SetValue(value);
        }

        [LinkedObject]
        public TestWidget? SecondaryWidget
        {
            get => GetValue<TestWidget>();
            set => SetValue(value);
        }
    }

    /// <summary>
    /// Covers the linked-object schema surface: population of linked-object properties (two
    /// properties of the SAME linked type must both register) and the GetLinkedObjects walk.
    /// </summary>
    public class LinkedObjectSchemaTests
    {
        [Fact]
        public void Schema_RegistersAllLinkedObjectProperties()
        {
            var schema = ObjectSchema<TestInvoice>.Get();

            Assert.True(schema.HasLinkedObjects);
            Assert.Equal(2, schema.LinkedObjectProperties.Length);
        }

        [Fact]
        public void GetLinkedObjects_ReturnsBoundLinkedObjects()
        {
            IObjectSchema schema = ObjectSchema<TestInvoice>.Get();
            var invoice = new TestInvoice
            {
                Reference = "INV-1",
                PrimaryWidget = new TestWidget { Name = "P" },
            };

            var linked = schema.GetLinkedObjects(invoice).ToList();

            var widget = Assert.Single(linked);
            Assert.Equal("P", ((TestWidget)widget).Name);
        }

        [Fact]
        public void GetLinkedObjects_NoValuesBound_ReturnsEmpty()
        {
            IObjectSchema schema = ObjectSchema<TestInvoice>.Get();
            var invoice = new TestInvoice { Reference = "INV-2" };

            Assert.Empty(schema.GetLinkedObjects(invoice));
        }
    }
}
