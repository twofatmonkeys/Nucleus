namespace TFM.Nucleus.Tests
{
    public class DataSetTests
    {
        private static readonly string[] Columns = ["ID", "Name", "Quantity"];

        private static TFM.Nucleus.DataSet NewDataSet() => TFM.Nucleus.DataSet.Initialize("TestWidget", Columns);

        [Fact]
        public void Initialize_SetsNameAndColumns()
        {
            using var ds = NewDataSet();

            Assert.Equal("TestWidget", ds.Name);
            Assert.Equal(3, ds.ColumnCount);
            Assert.Equal(Columns, ds.ColumnNames);
            Assert.Equal(0, ds.RowCount);
            Assert.False(ds.HasData);
        }

        [Fact]
        public void FillNext_StoresRowsInOrder()
        {
            using var ds = NewDataSet();
            var idA = Guid.NewGuid();
            var idB = Guid.NewGuid();

            ds.FillNext([idA, "A", 1]);
            ds.FillNext([idB, "B", 2]);

            Assert.Equal(2, ds.RowCount);
            Assert.True(ds.HasData);
            Assert.Equal("A", ds[0][1]);
            Assert.Equal("B", ds[1][1]);
            Assert.Equal(idB, ds[1][0]);
        }

        [Fact]
        public void FillNext_WrongColumnCount_Throws()
        {
            using var ds = NewDataSet();

            Assert.Throws<ArgumentException>(() => ds.FillNext([Guid.NewGuid(), "A"]));
        }

        [Fact]
        public void GetColumn_ByName_ReturnsValue()
        {
            using var ds = NewDataSet();
            ds.FillNext([Guid.NewGuid(), "A", 7]);

            Assert.Equal(7, ds.GetColumn(0, "Quantity"));
        }

        [Fact]
        public void GetRow_ExposesNamedValues()
        {
            using var ds = NewDataSet();
            ds.FillNext([Guid.NewGuid(), "A", 7]);

            var row = ds.GetRow(0);
            var values = row.Values;

            Assert.Equal(3, values.Length);
            Assert.Equal("Name", values[1].ColumnName);
            Assert.Equal("A", values[1].Value);
        }

        [Fact]
        public void FillNext_AcrossChunkBoundary_StoresAllRows()
        {
            // Rows are stored in 10,000-row blocks; cross the first boundary.
            using var ds = NewDataSet();
            const int total = 10_001;

            for (int i = 0; i < total; i++)
                ds.FillNext([Guid.Empty, $"row{i}", i]);

            Assert.Equal(total, ds.RowCount);
            Assert.Equal("row0", ds[0][1]);
            Assert.Equal("row9999", ds[9_999][1]);
            Assert.Equal("row10000", ds[10_000][1]);
        }

        [Fact]
        public void Bind_MapsColumnsToEntityProperties()
        {
            using var ds = NewDataSet();
            var id = Guid.NewGuid();
            ds.FillNext([id, "Widget A", 5]);

            var widgets = ds.Bind<TestWidget>().ToList();

            var widget = Assert.Single(widgets);
            Assert.Equal("Widget A", widget.Name);
            Assert.Equal(5, widget.Quantity);
        }

        [Fact]
        public void Bind_EmptyDataSet_ReturnsEmpty()
        {
            using var ds = NewDataSet();

            Assert.Empty(ds.Bind<TestWidget>());
        }
    }
}
