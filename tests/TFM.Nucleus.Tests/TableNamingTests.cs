using Microsoft.Extensions.Logging;

namespace TFM.Nucleus.Tests.NamingA
{
    /// <summary>Shares its class name with <see cref="NamingB.TestSameNameWidget"/>, so ObjectName(1) gives both one table.</summary>
    public class TestSameNameWidget : CoreObject<TestSameNameWidget>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }
}

namespace TFM.Nucleus.Tests.NamingB
{
    /// <summary>Shares its class name with <see cref="NamingA.TestSameNameWidget"/>.</summary>
    public class TestSameNameWidget : CoreObject<TestSameNameWidget>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }
}

namespace TFM.Nucleus.Tests
{
    /// <summary>An entity that names nothing itself, so its provider's policy names its table.</summary>
    public class TestNamingWidget : CoreObject<TestNamingWidget>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    /// <summary>An entity whose table name is pinned, so no policy moves it.</summary>
    [DatabaseTable("tbl_PinnedWidget")]
    public class TestPinnedWidget : CoreObject<TestPinnedWidget>
    {
        [DatabaseColumn(Indexed = true)]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    /// <summary>A pinned name with a space in it. Must fail at schema build.</summary>
    [DatabaseTable("tbl_Pinned Widget")]
    public class TestBadPinnedWidget : CoreObject<TestBadPinnedWidget>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    /// <summary>Pins the table name <see cref="TestNamingWidget"/> has under the full-name policy.</summary>
    [DatabaseTable("tbl_TFM_Nucleus_Tests_TestNamingWidget")]
    public class TestDuplicatePinnedWidget : CoreObject<TestDuplicatePinnedWidget>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    /// <summary>A root whose naming policy every entity under it inherits.</summary>
    [TableNaming(TableNamingStyle.ObjectName, 2)]
    public abstract class TestNamingRoot<T> : CoreObject<T> where T : CoreObject<T> { }

    public class TestRootedWidget : TestNamingRoot<TestRootedWidget>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    /// <summary>Under the ObjectName(2) root, but declares the full-name policy itself.</summary>
    [TableNaming(TableNamingStyle.FullName)]
    public class TestRootedFullWidget : TestNamingRoot<TestRootedFullWidget>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    /// <summary>ObjectName with no levels. Must fail at schema build.</summary>
    [TableNaming(TableNamingStyle.ObjectName, 0)]
    public class TestZeroLevelWidget : CoreObject<TestZeroLevelWidget>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    /// <summary>A root with a pinned name, which the entities under it must not inherit.</summary>
    [DatabaseTable("tbl_PinnedRoot")]
    public abstract class TestPinnedRoot<T> : CoreObject<T> where T : CoreObject<T> { }

    public class TestUnderPinnedRoot : TestPinnedRoot<TestUnderPinnedRoot>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    /// <summary>An entity whose table had two earlier names.</summary>
    [LegacyTableNames("tbl_OldNamingWidget", "tbl_OlderNamingWidget")]
    public class TestLegacyWidget : CoreObject<TestLegacyWidget>
    {
        [DatabaseColumn(Indexed = true)]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn]
        public int Quantity { get => GetValue<int>(); set => SetValue(value); }
    }

    /// <summary>Claims one of <see cref="TestLegacyWidget"/>'s old names as its own.</summary>
    [LegacyTableNames("tbl_OldNamingWidget")]
    public class TestRivalLegacyWidget : CoreObject<TestRivalLegacyWidget>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    /// <summary>Claims <see cref="TestNamingWidget"/>'s live table as an old name.</summary>
    [LegacyTableNames("tbl_TFM_Nucleus_Tests_TestNamingWidget")]
    public class TestStealingWidget : CoreObject<TestStealingWidget>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    /// <summary>Lists its own table name as an old name. Must fail at schema build.</summary>
    [DatabaseTable("tbl_SelfLegacy")]
    [LegacyTableNames("tbl_SelfLegacy")]
    public class TestSelfLegacyWidget : CoreObject<TestSelfLegacyWidget>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    /// <summary>
    /// How tables are named: the provider's policy, an entity's own [TableNaming], and a pinned
    /// [DatabaseTable] name, in rising precedence; and how the schema manager adopts a table
    /// under an old name.
    /// </summary>
    public class TableNamingTests
    {
        public class NestedWidget : CoreObject<NestedWidget>
        {
            [DatabaseColumn]
            public string? Name { get => GetValue<string>(); set => SetValue(value); }
        }

        private static TableSchema Table<T>(TableNamingPolicy? policy = null) where T : CoreObject<T> =>
            new(ObjectSchema.Get<T>(), policy ?? TableNamingPolicy.FullName);

        // ---- Policies

        [Fact]
        public void FullName_IsTheNameTablesHadBeforePolicies()
        {
            Assert.Equal("tbl_TFM_Nucleus_Tests_TestNamingWidget", TableNamingPolicy.FullName.NameFor(typeof(TestNamingWidget)));
            Assert.Equal("tbl_TFM_Nucleus_Tests_TableNamingTests_NestedWidget", TableNamingPolicy.FullName.NameFor(typeof(NestedWidget)));
            Assert.Equal("tbl_TFM_Nucleus_Tests_TestNamingWidget", TableSchema.LogicalName(ObjectSchema.Get<TestNamingWidget>()));
            Assert.Equal("tbl_TFM_Nucleus_Tests_TestNamingWidget", new TableSchema(ObjectSchema.Get<TestNamingWidget>()).TableName);
        }

        [Theory]
        [InlineData(1, "tbl_TestNamingWidget")]
        [InlineData(2, "tbl_Tests_TestNamingWidget")]
        [InlineData(3, "tbl_Nucleus_Tests_TestNamingWidget")]
        [InlineData(4, "tbl_TFM_Nucleus_Tests_TestNamingWidget")]
        [InlineData(99, "tbl_TFM_Nucleus_Tests_TestNamingWidget")]
        public void ObjectName_KeepsTheLastLevels(int levels, string expected) =>
            Assert.Equal(expected, TableNamingPolicy.ObjectName(levels).NameFor(typeof(TestNamingWidget)));

        [Fact]
        public void ObjectName_CountsANestedTypesOuterTypeAsALevel()
        {
            Assert.Equal("tbl_NestedWidget", TableNamingPolicy.ObjectName(1).NameFor(typeof(NestedWidget)));
            Assert.Equal("tbl_TableNamingTests_NestedWidget", TableNamingPolicy.ObjectName(2).NameFor(typeof(NestedWidget)));
        }

        [Fact]
        public void ObjectName_Two_NamesABinderyDocument_AsTheExampleSays() =>
            Assert.Equal("tbl_Bindery_BinderyDocument", TableNamingPolicy.ObjectName(2).NameFor(typeof(TFM.Nucleus.Bindery.BinderyDocument)));

        [Fact]
        public void ObjectName_RefusesFewerThanOneLevel()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => TableNamingPolicy.ObjectName(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => TableNamingPolicy.From(new TableNamingAttribute(TableNamingStyle.ObjectName, -1)));
        }

        [Fact]
        public void From_MapsTheAttribute()
        {
            Assert.Equal(TableNamingPolicy.FullName, TableNamingPolicy.From(new TableNamingAttribute(TableNamingStyle.FullName, 5)));
            Assert.Equal(TableNamingPolicy.ObjectName(3), TableNamingPolicy.From(new TableNamingAttribute(TableNamingStyle.ObjectName, 3)));
        }

        [Fact]
        public void TheProviderOptions_DefaultToTheFullName_AndRefuseNoPolicy()
        {
            var options = new SQLConfiguration();

            Assert.Equal(TableNamingPolicy.FullName, options.TableNaming);
            Assert.False(options.RenameFullNameTables);
            Assert.Throws<ArgumentNullException>(() => options.TableNaming = null!);
        }

        [Fact]
        public void TheProviderPolicy_NamesATypeThatNamesNothing() =>
            Assert.Equal("tbl_TestNamingWidget", Table<TestNamingWidget>(TableNamingPolicy.ObjectName()).TableName);

        // ---- [DatabaseTable]

        [Fact]
        public void DatabaseTable_WinsOverEveryPolicy_AndNamesTheIndexes()
        {
            var table = Table<TestPinnedWidget>(TableNamingPolicy.ObjectName(2));

            Assert.Equal("tbl_PinnedWidget", table.TableName);
            Assert.Equal("tbl_PinnedWidget", TableSchema.LogicalName(ObjectSchema.Get<TestPinnedWidget>()));
            Assert.Equal("NCI_tbl_PinnedWidget_Name", IndexNaming.ColumnIndexName(table, table["Name"]));
            Assert.Equal("tbl_TFM_Nucleus_Tests_TestPinnedWidget", table.FullTableName);
        }

        [Fact]
        public void DatabaseTable_RefusesANameThatIsNotAnIdentifier()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Table<TestBadPinnedWidget>());

            Assert.Contains(nameof(TestBadPinnedWidget), ex.Message);
            Assert.Contains("tbl_Pinned Widget", ex.Message);
        }

        [Fact]
        public void DatabaseTable_OnARoot_IsNotInherited() =>
            Assert.Equal("tbl_TFM_Nucleus_Tests_TestUnderPinnedRoot", Table<TestUnderPinnedRoot>().TableName);

        // ---- [TableNaming]

        [Fact]
        public void TableNaming_OnARoot_IsInherited_AndWinsOverTheProviderPolicy()
        {
            Assert.Equal("tbl_Tests_TestRootedWidget", Table<TestRootedWidget>().TableName);
            Assert.Equal("tbl_Tests_TestRootedWidget", Table<TestRootedWidget>(TableNamingPolicy.ObjectName(1)).TableName);
        }

        [Fact]
        public void TableNaming_OnTheTypeItself_WinsOverItsRoots() =>
            Assert.Equal("tbl_TFM_Nucleus_Tests_TestRootedFullWidget", Table<TestRootedFullWidget>(TableNamingPolicy.ObjectName(1)).TableName);

        [Fact]
        public void TableNaming_RefusesNoLevels()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Table<TestZeroLevelWidget>());

            Assert.Contains(nameof(TestZeroLevelWidget), ex.Message);
        }

        // ---- [LegacyTableNames]

        [Fact]
        public void LegacyTableNames_AreKeptInDeclarationOrder() =>
            Assert.Equal(["tbl_OldNamingWidget", "tbl_OlderNamingWidget"], Table<TestLegacyWidget>().LegacyTableNames);

        [Fact]
        public void LegacyTableNames_RefuseTheTablesOwnName()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Table<TestSelfLegacyWidget>());

            Assert.Contains("own name", ex.Message);
        }

        // ---- Schema build

        [Fact]
        public void TwoTypesThePolicyGivesOneName_FailAtSchemaBuild()
        {
            var store = new RecordingSchemaStore(new CatalogSnapshot([], [], []), typeof(NamingA.TestSameNameWidget), typeof(NamingB.TestSameNameWidget))
            {
                TableNaming = TableNamingPolicy.ObjectName(1),
            };

            var ex = Assert.Throws<InvalidOperationException>(() => store.Manager().BuildSchema());

            Assert.Contains("TFM.Nucleus.Tests.NamingA.TestSameNameWidget", ex.Message);
            Assert.Contains("TFM.Nucleus.Tests.NamingB.TestSameNameWidget", ex.Message);
            Assert.Contains("tbl_TestSameNameWidget", ex.Message);
        }

        [Fact]
        public void TwoTypesTheFullNamePolicyKeepsApart_BuildTogether() =>
            Assert.Equal(2, new RecordingSchemaStore(new CatalogSnapshot([], [], []), typeof(NamingA.TestSameNameWidget), typeof(NamingB.TestSameNameWidget))
                .Manager().BuildSchema().Count);

        [Fact]
        public void APinnedNameAnotherTypeHas_FailsAtSchemaBuild()
        {
            var store = new RecordingSchemaStore(new CatalogSnapshot([], [], []), typeof(TestNamingWidget), typeof(TestDuplicatePinnedWidget));

            var ex = Assert.Throws<InvalidOperationException>(() => store.Manager().BuildSchema());

            Assert.Contains(nameof(TestDuplicatePinnedWidget), ex.Message);
            Assert.Contains(nameof(TestNamingWidget), ex.Message);
        }

        [Fact]
        public void AnOldNameThatIsAnotherTypesTable_FailsAtSchemaBuild()
        {
            var store = new RecordingSchemaStore(new CatalogSnapshot([], [], []), typeof(TestNamingWidget), typeof(TestStealingWidget));

            var ex = Assert.Throws<InvalidOperationException>(() => store.Manager().BuildSchema());

            Assert.Contains("would adopt table 'tbl_TFM_Nucleus_Tests_TestNamingWidget'", ex.Message);
        }

        [Fact]
        public void AFullNameAnotherTypeHasPinned_FailsAtSchemaBuild_OnlyWhenFullNameTablesAreRenamed()
        {
            // TestNamingWidget's full-name form is TestDuplicatePinnedWidget's pinned table; under
            // ObjectName(1) the two tables differ, but adopting the full-name table would take the other's rows.
            Type[] types = [typeof(TestNamingWidget), typeof(TestDuplicatePinnedWidget)];
            var keep = new RecordingSchemaStore(new CatalogSnapshot([], [], []), types) { TableNaming = TableNamingPolicy.ObjectName(1) };
            var rename = new RecordingSchemaStore(new CatalogSnapshot([], [], []), types) { TableNaming = TableNamingPolicy.ObjectName(1), RenameFullNameTables = true };

            Assert.Equal(2, keep.Manager().BuildSchema().Count);
            Assert.Throws<InvalidOperationException>(() => rename.Manager().BuildSchema());
        }

        [Fact]
        public void AnOldNameTwoTypesClaim_FailsAtSchemaBuild()
        {
            var store = new RecordingSchemaStore(new CatalogSnapshot([], [], []), typeof(TestLegacyWidget), typeof(TestRivalLegacyWidget));

            var ex = Assert.Throws<InvalidOperationException>(() => store.Manager().BuildSchema());

            Assert.Contains("both claim table 'tbl_OldNamingWidget'", ex.Message);
        }

        // ---- Adoption

        private static readonly SqlServerSchemaDialect SqlDialect = new(null);

        /// <summary>The catalog <paramref name="table"/> would have under <paramref name="name"/>, without the named columns.</summary>
        private static CatalogSnapshot OldTable(TableSchema table, string name, params string[] without)
        {
            var current = SchemaCatalog.Current("sqlserver", table);
            return new CatalogSnapshot(
                [name],
                [.. current.Columns.Where(c => !without.Contains(c.Name)).Select(c => new CatalogColumn { TableName = name, Name = c.Name, TypeName = c.TypeName, DataLength = c.DataLength, Precision = c.Precision, Scale = c.Scale, IsNullable = c.IsNullable })],
                []);
        }

        private static CatalogSnapshot Merge(params CatalogSnapshot[] snapshots) => new(
            [.. snapshots.SelectMany(s => s.Tables)], [.. snapshots.SelectMany(s => s.Columns)], [.. snapshots.SelectMany(s => s.Indexes)]);

        /// <summary>After a rename the new table has the old table's columns.</summary>
        private static Func<string, IReadOnlyList<CatalogColumn>> RenamedFrom(CatalogSnapshot old) =>
            name => [.. old.Columns.Select(c => new CatalogColumn { TableName = name, Name = c.Name, TypeName = c.TypeName, DataLength = c.DataLength, Precision = c.Precision, Scale = c.Scale, IsNullable = c.IsNullable })];

        [Fact]
        public void AMissingTable_IsAdoptedFromItsOneOldTable_ThenGetsTheColumnsItLacks()
        {
            var table = Table<TestLegacyWidget>();
            var old = OldTable(table, "tbl_OlderNamingWidget", "Quantity");
            var store = new RecordingSchemaStore(old, typeof(TestLegacyWidget)) { ColumnsInTransaction = RenamedFrom(old) };

            Assert.Equal(EDataSchemaStatus.Ok, store.Manager().Initialize());

            var adopt = SqlDialect.AdoptTable("tbl_OlderNamingWidget", table).Select(s => s.Text).ToList();
            Assert.Equal([.. adopt, SqlServerSchemaDialect.AddColumnsSql(table, [table["Quantity"]], [])], store.InTransaction);
            Assert.DoesNotContain(store.InTransaction, s => s.StartsWith("CREATE TABLE", StringComparison.Ordinal));
            Assert.Contains(store.Logged, l => l.Level == LogLevel.Information && l.Message.Contains("renames its old table tbl_OlderNamingWidget"));
        }

        [Fact]
        public void TheFullNameTable_IsAdopted_WhenTheProviderRenamesFullNameTables()
        {
            var table = Table<TestNamingWidget>(TableNamingPolicy.ObjectName(1));
            var old = OldTable(table, "tbl_TFM_Nucleus_Tests_TestNamingWidget");
            var store = new RecordingSchemaStore(old, typeof(TestNamingWidget))
            {
                TableNaming = TableNamingPolicy.ObjectName(1),
                RenameFullNameTables = true,
                ColumnsInTransaction = RenamedFrom(old),
            };

            Assert.Equal(EDataSchemaStatus.Ok, store.Manager().Initialize());

            Assert.Equal(SqlDialect.AdoptTable("tbl_TFM_Nucleus_Tests_TestNamingWidget", table).Select(s => s.Text), store.InTransaction);
            Assert.StartsWith("EXEC sp_rename 'tbl_TFM_Nucleus_Tests_TestNamingWidget', 'tbl_TestNamingWidget'", store.InTransaction[0], StringComparison.Ordinal);
        }

        [Fact]
        public void TheFullNameTable_IsLeftAlone_WhenTheProviderDoesNotRenameFullNameTables()
        {
            var table = Table<TestNamingWidget>(TableNamingPolicy.ObjectName(1));
            var store = new RecordingSchemaStore(OldTable(table, "tbl_TFM_Nucleus_Tests_TestNamingWidget"), typeof(TestNamingWidget))
            {
                TableNaming = TableNamingPolicy.ObjectName(1),
            };

            Assert.Equal(EDataSchemaStatus.Ok, store.Manager().Initialize());

            Assert.StartsWith("CREATE TABLE [dbo].[tbl_TestNamingWidget]", store.InTransaction[0], StringComparison.Ordinal);
            Assert.DoesNotContain(store.InTransaction, s => s.Contains("sp_rename", StringComparison.Ordinal));
        }

        [Fact]
        public void SeveralOldTables_AreNotMerged_TheUpdateFails_AndNothingRuns()
        {
            var table = Table<TestLegacyWidget>();
            var store = new RecordingSchemaStore(
                Merge(OldTable(table, "tbl_OldNamingWidget"), OldTable(table, "tbl_OlderNamingWidget")), typeof(TestLegacyWidget));

            Assert.Equal(EDataSchemaStatus.Error, store.Manager().Initialize());

            Assert.Empty(store.InTransaction);
            Assert.Empty(store.OutsideTransaction);
            Assert.True(store.RolledBack);
            Assert.Contains(store.Logged, l => l.Level == LogLevel.Error &&
                l.Message.Contains("tbl_OldNamingWidget, tbl_OlderNamingWidget") && l.Message.Contains("does not merge"));
        }

        [Fact]
        public void AnOldTableBesideItsSuccessor_IsLeftAlone_WithAWarning()
        {
            var table = Table<TestLegacyWidget>();
            var store = new RecordingSchemaStore(Merge(SchemaCatalog.Current("sqlserver", table), OldTable(table, "tbl_OldNamingWidget")), typeof(TestLegacyWidget));

            Assert.Equal(EDataSchemaStatus.Ok, store.Manager().Initialize());

            Assert.Empty(store.InTransaction);
            Assert.Contains(store.Logged, l => l.Level == LogLevel.Warning && l.Message.Contains("old table tbl_OldNamingWidget is left as it is"));
        }
    }
}
