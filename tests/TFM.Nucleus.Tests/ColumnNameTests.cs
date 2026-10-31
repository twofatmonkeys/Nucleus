namespace TFM.Nucleus.Tests
{
    /// <summary>Two properties claiming one column, differing only in case. Must fail at schema build.</summary>
    public class TestDuplicateColumnWidget : CoreObject<TestDuplicateColumnWidget>
    {
        [DatabaseColumn("code")]
        public string? First { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn("CODE")]
        public string? Second { get => GetValue<string>(); set => SetValue(value); }
    }

    /// <summary>A property claiming the column a table keeps for its rebuild order. Must fail at schema build.</summary>
    public class TestClusterIdColumnWidget : CoreObject<TestClusterIdColumnWidget>
    {
        [DatabaseColumn("ClusterID")]
        public int Sequence { get => GetValue<int>(); set => SetValue(value); }
    }

    /// <summary>A soft-deletable type claiming its own IsDeleted column. Must fail at schema build.</summary>
    [Deletable(DeleteBehavior.Soft)]
    public class TestSoftDeletedColumnWidget : CoreObject<TestSoftDeletedColumnWidget>
    {
        [DatabaseColumn("isdeleted")]
        public bool Archived { get => GetValue<bool>(); set => SetValue(value); }
    }

    /// <summary>
    /// [DatabaseColumn]'s ColumnName names the physical column everywhere a statement writes it,
    /// while values still bind to the entity by property name.
    /// </summary>
    public class ColumnNameTests
    {
        private static TableSchema Renamed => new(ObjectSchema.Get<TestRenamedWidget>());

        [Fact]
        public void ColumnName_NamesTheColumn_AndKeepsThePropertyName()
        {
            var title = Renamed.FindByPropertyName(nameof(TestRenamedWidget.Title))!;

            Assert.Equal("title_text", title.ColumnName);
            Assert.Equal(nameof(TestRenamedWidget.Title), title.PropertyName);
            Assert.Equal(title.PropertyName, Renamed.FindByColumnName("title_text")!.PropertyName);
        }

        [Fact]
        public void ColumnName_GivenToTheConstructor_NamesTheColumn() =>
            Assert.Equal("item_code", Renamed.FindByPropertyName(nameof(TestRenamedWidget.Code))!.ColumnName);

        [Fact]
        public void ColumnName_NotGiven_IsThePropertyName() =>
            Assert.Equal(nameof(TestRenamedWidget.Quantity), Renamed.FindByPropertyName(nameof(TestRenamedWidget.Quantity))!.ColumnName);

        [Fact]
        public void IndexName_UsesTheColumnName()
        {
            var table = Renamed;

            Assert.Equal("NCI_tbl_TFM_Nucleus_Tests_TestRenamedWidget_title_text", IndexNaming.ColumnIndexName(table, table.FindByPropertyName("Title")!));
            Assert.Equal("UQ_tbl_TFM_Nucleus_Tests_TestRenamedWidget_item_code", IndexNaming.ColumnIndexName(table, table.FindByPropertyName("Code")!));
        }

        [Fact]
        public void DataSet_LabelsValuesByPropertyName_SoTheyBindToTheEntity()
        {
            var set = DataSet.Initialize(Renamed);

            Assert.Contains("Title", set.ColumnNames);
            Assert.Contains("Code", set.ColumnNames);
            Assert.DoesNotContain("title_text", set.ColumnNames);
        }

        [Fact]
        public void TwoPropertiesOnOneColumn_FailAtSchemaBuild()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => new TableSchema(ObjectSchema.Get<TestDuplicateColumnWidget>()));

            Assert.Contains("First", ex.Message);
            Assert.Contains("Second", ex.Message);
        }

        [Fact]
        public void AColumnNamedLikeTheClusterId_FailsAtSchemaBuild()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => new TableSchema(ObjectSchema.Get<TestClusterIdColumnWidget>()));

            Assert.Contains("Sequence", ex.Message);
            Assert.Contains("ClusterID", ex.Message);
        }

        [Fact]
        public void ASoftDeleteColumnName_OnASoftDeletableType_FailsAtSchemaBuild()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => new TableSchema(ObjectSchema.Get<TestSoftDeletedColumnWidget>()));

            Assert.Contains("Archived", ex.Message);
        }

        [Fact]
        public void UniqueViolation_MatchesThePropertyOrTheColumnName()
        {
            var violation = new UniqueViolationException("m", "UQ_x", typeof(TestRenamedWidget), "Code", "item_code", new Exception());

            Assert.True(violation.Is<TestRenamedWidget>("Code"));
            Assert.True(violation.Is<TestRenamedWidget>("item_code"));
            Assert.False(violation.Is<TestRenamedWidget>("Title"));
            Assert.False(violation.Is<TestWidget>("Code"));
        }
    }
}
