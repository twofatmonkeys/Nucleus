namespace TFM.Nucleus.Tests
{
    /// <summary>A table with one index over two keys carrying two more columns, one of them long text.</summary>
    [TableIndex("NameQuantity", nameof(Name), nameof(Quantity), Include = [nameof(Code), nameof(Notes)])]
    public class TestIndexedWidget : CoreObject<TestIndexedWidget>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn]
        public int Quantity { get => GetValue<int>(); set => SetValue(value); }

        [DatabaseColumn]
        public string? Code { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(DataType.Unicode, -1)]
        public string? Notes { get => GetValue<string>(); set => SetValue(value); }
    }

    /// <summary>Two unique indexes over two keys each, one of them carrying a long text column.</summary>
    [TableIndex("CodeQuantity", nameof(Code), nameof(Quantity), Unique = true)]
    [TableIndex("NameCode", nameof(Name), nameof(Code), Unique = true, Include = [nameof(Notes)])]
    public class TestUniqueIndexedWidget : CoreObject<TestUniqueIndexedWidget>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn]
        public int Quantity { get => GetValue<int>(); set => SetValue(value); }

        [DatabaseColumn(Unique = true)]
        public string? Code { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(DataType.Unicode, -1)]
        public string? Notes { get => GetValue<string>(); set => SetValue(value); }
    }

    [TableIndex("Bad", "Missing")]
    public class TestBadIndexUnknownProperty : CoreObject<TestBadIndexUnknownProperty>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    [TableIndex("Bad", nameof(Notes))]
    public class TestBadIndexLongKey : CoreObject<TestBadIndexLongKey>
    {
        [DatabaseColumn(DataType.Unicode, -1)]
        public string? Notes { get => GetValue<string>(); set => SetValue(value); }
    }

    [TableIndex("Same", nameof(Name))]
    [TableIndex("same", nameof(Name))]
    public class TestBadIndexDuplicateName : CoreObject<TestBadIndexDuplicateName>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    [TableIndex("Bad", nameof(Name), Include = [nameof(Name)])]
    public class TestBadIndexRepeatedColumn : CoreObject<TestBadIndexRepeatedColumn>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    [TableIndex("not a name", nameof(Name))]
    public class TestBadIndexName : CoreObject<TestBadIndexName>
    {
        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }
    }

    /// <summary>
    /// [TableIndex]: the schema built from the declaration, the refusals at schema build, and the
    /// compare behind schema sync, with no database. The DDL itself is pinned by the raw goldens
    /// (ddl.tableindex).
    /// </summary>
    public class TableIndexTests
    {
        private const string Table = "tbl_TFM_Nucleus_Tests_TestIndexedWidget";
        private const string Index = $"NIX_{Table}_NameQuantity";

        private static readonly SqlServerSchemaDialect Dialect = new(null);

        private static TableSchema Schema() => new(ObjectSchema.Get<TestIndexedWidget>());

        private static CatalogIndex Row(string column, int ordinal, bool included = false, string index = Index, bool unique = false, bool filter = false, string table = Table) => new()
        {
            TableName = table,
            IndexName = index,
            ColumnName = column,
            KeyOrdinal = ordinal,
            IsIncluded = included,
            IsUnique = unique,
            HasFilter = filter,
        };

        private const string UniqueTable = "tbl_TFM_Nucleus_Tests_TestUniqueIndexedWidget";
        private const string UniqueIndex = $"NIX_{UniqueTable}_CodeQuantity";

        private static TableSchema UniqueSchema() => new(ObjectSchema.Get<TestUniqueIndexedWidget>());

        // The CodeQuantity index of TestUniqueIndexedWidget as the catalog reads it; NameCode is left out, so it is always created.
        private static List<CatalogIndex> UniqueRows(bool unique, bool filter) =>
        [
            Row("Code", 1, index: UniqueIndex, unique: unique, filter: filter, table: UniqueTable),
            Row("Quantity", 2, index: UniqueIndex, unique: unique, filter: filter, table: UniqueTable),
        ];

        private static List<CatalogIndex> Matching() =>
        [
            Row("Name", 1),
            Row("Quantity", 2),
            Row("Notes", 0, included: true),
            Row("Code", 0, included: true),
        ];

        [Fact]
        public void Declaration_BuildsKeysInOrderAndIncludedColumns()
        {
            var index = Assert.Single(Schema().TableIndexes);

            Assert.Equal("NameQuantity", index.Name);
            Assert.Equal(["Name", "Quantity"], index.Keys.Select(k => k.ColumnName));
            Assert.Equal(["Code", "Notes"], index.Include.Select(c => c.ColumnName));
            Assert.Equal(Index, IndexNaming.TableIndexName(Schema(), index));
        }

        [Fact]
        public void Declaration_IsPlainUnlessUnique()
        {
            Assert.False(Assert.Single(Schema().TableIndexes).Unique);
            Assert.All(UniqueSchema().TableIndexes, i => Assert.True(i.Unique));
        }

        [Fact]
        public void UniqueDeclaration_WithIncludedColumns_IsAccepted()
        {
            var index = UniqueSchema().TableIndexes.Single(i => i.Name == "NameCode");

            Assert.True(index.Unique);
            Assert.Equal(["Name", "Code"], index.Keys.Select(k => k.ColumnName));
            Assert.Equal(["Notes"], index.Include.Select(c => c.ColumnName));
        }

        [Fact]
        public void Diff_DeclaredUnique_ButPlainInTheDatabase_DropsAndRecreates()
        {
            var (drop, create) = SchemaComparer.DiffTableIndexes(UniqueSchema(), UniqueRows(unique: false, filter: false), Dialect);

            Assert.Equal([UniqueIndex], drop);
            Assert.Contains(create, i => i.Name == "CodeQuantity");
        }

        [Fact]
        public void Diff_DeclaredUnique_ButUnfilteredInTheDatabase_DropsAndRecreates()
        {
            var (drop, create) = SchemaComparer.DiffTableIndexes(UniqueSchema(), UniqueRows(unique: true, filter: false), Dialect);

            Assert.Equal([UniqueIndex], drop);
            Assert.Contains(create, i => i.Name == "CodeQuantity");
        }

        [Fact]
        public void Diff_MatchingUniqueFilteredIndex_IsLeftAlone()
        {
            var (drop, create) = SchemaComparer.DiffTableIndexes(UniqueSchema(), UniqueRows(unique: true, filter: true), Dialect);

            Assert.Empty(drop);
            Assert.DoesNotContain(create, i => i.Name == "CodeQuantity");
        }

        [Fact]
        public void Diff_DeclaredPlain_ButUniqueInTheDatabase_DropsAndRecreates()
        {
            var existing = Matching();
            existing.ForEach(r => { r.IsUnique = true; r.HasFilter = true; });

            var (drop, create) = SchemaComparer.DiffTableIndexes(Schema(), existing, Dialect);

            Assert.Equal([Index], drop);
            Assert.Single(create);
        }

        [Theory]
        [InlineData(typeof(TestBadIndexUnknownProperty), "not a stored column")]
        [InlineData(typeof(TestBadIndexLongKey), "long text column")]
        [InlineData(typeof(TestBadIndexDuplicateName), "already has")]
        [InlineData(typeof(TestBadIndexRepeatedColumn), "more than once")]
        [InlineData(typeof(TestBadIndexName), "letters, digits and underscores")]
        public void BadDeclaration_FailsAtSchemaBuild(Type type, string reason)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => new TableSchema(ObjectSchema.Get(type)));

            Assert.Contains(reason, ex.Message);
        }

        [Fact]
        public void Diff_EmptyDatabase_CreatesTheIndex()
        {
            var (drop, create) = SchemaComparer.DiffTableIndexes(Schema(), [], Dialect);

            Assert.Empty(drop);
            Assert.Equal("NameQuantity", Assert.Single(create).Name);
        }

        [Fact]
        public void Diff_MatchingIndex_DoesNothing_WhateverOrderTheIncludedColumnsComeIn()
        {
            var (drop, create) = SchemaComparer.DiffTableIndexes(Schema(), Matching(), Dialect);

            Assert.Empty(drop);
            Assert.Empty(create);
        }

        [Fact]
        public void Diff_KeysInAnotherOrder_DropsAndRecreates()
        {
            List<CatalogIndex> existing = [Row("Quantity", 1), Row("Name", 2), Row("Code", 0, true), Row("Notes", 0, true)];

            var (drop, create) = SchemaComparer.DiffTableIndexes(Schema(), existing, Dialect);

            Assert.Equal([Index], drop);
            Assert.Single(create);
        }

        [Fact]
        public void Diff_AnIncludedColumnMissing_DropsAndRecreates()
        {
            var existing = Matching();
            existing.RemoveAt(3);

            var (drop, create) = SchemaComparer.DiffTableIndexes(Schema(), existing, Dialect);

            Assert.Equal([Index], drop);
            Assert.Single(create);
        }

        [Fact]
        public void Diff_IndexNoLongerDeclared_IsDropped()
        {
            var existing = Matching();
            existing.Add(Row("Name", 1, index: $"NIX_{Table}_Removed"));

            var (drop, create) = SchemaComparer.DiffTableIndexes(Schema(), existing, Dialect);

            Assert.Equal([$"NIX_{Table}_Removed"], drop);
            Assert.Empty(create);
        }

        [Fact]
        public void Diff_HandMadeIndexWithACommonPrefix_IsNeverTouched()
        {
            // The catalog readers return only NCI_, UQ_ and NIX_ indexes; even handed an IX_ one,
            // the table index compare leaves it alone.
            var existing = Matching();
            existing.Add(Row("Name", 1, index: $"IX_{Table}_ByHand"));

            var (drop, create) = SchemaComparer.DiffTableIndexes(Schema(), existing, Dialect);

            Assert.Empty(drop);
            Assert.Empty(create);
        }

        [Fact]
        public void ColumnIndexDiff_LeavesTableIndexesAlone()
        {
            // The table's only column index is the inherited Timestamp one.
            List<CatalogIndex> existing =
            [
                .. Matching(),
                new() { TableName = Table, IndexName = $"NCI_{Table}_Timestamp", ColumnName = "Timestamp", IsDescending = true, KeyOrdinal = 1 },
            ];

            var (drop, create) = SchemaComparer.DiffColumnIndexes(Schema(), existing, Dialect);

            Assert.Empty(drop);
            Assert.Empty(create);
        }
    }
}
