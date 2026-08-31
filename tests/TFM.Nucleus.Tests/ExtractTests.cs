using System.Collections;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// End-to-end materialization from a DbDataReader through ObjectSchema.Extract.
    /// </summary>
    public class ExtractTests
    {
        private static readonly string[] Columns = ["ID", "Name", "Quantity"];

        [Fact]
        public async Task Extract_MaterializesRowsKeyedById()
        {
            var idA = Guid.NewGuid();
            var idB = Guid.NewGuid();
            var reader = new FakeDataReader(Columns,
            [
                [idA, "First", 1],
                [idB, "Second", 2],
            ]);
            var schema = ObjectSchema<TestWidget>.Get();

            IDictionary result = await schema.Extract(reader, CancellationToken.None);

            Assert.Equal(2, result.Count);
            var first = Assert.IsType<TestWidget>(result[idA]);
            var second = Assert.IsType<TestWidget>(result[idB]);
            Assert.Equal("First", first.Name);
            Assert.Equal(2, second.Quantity);
            Assert.False(first.IsNew);
        }

        [Fact]
        public async Task Extract_SkipsNullColumns()
        {
            var id = Guid.NewGuid();
            var reader = new FakeDataReader(Columns,
            [
                [id, DBNull.Value, 3],
            ]);
            var schema = ObjectSchema<TestWidget>.Get();

            IDictionary result = await schema.Extract(reader, CancellationToken.None);

            var widget = Assert.IsType<TestWidget>(result[id]);
            Assert.Null(widget.Name);
            Assert.Equal(3, widget.Quantity);
        }

        [Fact]
        public async Task Extract_EmptyReader_ReturnsEmpty()
        {
            var reader = new FakeDataReader(Columns, []);
            var schema = ObjectSchema<TestWidget>.Get();

            IDictionary result = await schema.Extract(reader, CancellationToken.None);

            Assert.Empty(result);
        }
    }
}
