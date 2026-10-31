namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// Column index DDL and the compare logic behind schema sync, with no SQL Server. The two
    /// golden strings pin the exact statements: the NonClusteredIndex one must never change
    /// (existing databases carry it), and the Unique one is the contract for new databases.
    /// </summary>
    public class SchemaIndexTests
    {
        private const string Table = "tbl_TFM_Nucleus_Tests_TestUniqueWidget";

        private static TableSchema UniqueSchema() => new(ObjectSchema.Get<TestUniqueWidget>());

        private static readonly SqlServerSchemaDialect Dialect = new(null);

        [Fact]
        public void Unique_RendersFilteredUniqueIndex_Golden()
        {
            var schema = UniqueSchema();

            var sql = SqlSnapshot.Normalize(SqlServerSchemaDialect.ColumnIndexSql(schema, schema["Code"]));

            Assert.Equal(
                $"CREATE UNIQUE NONCLUSTERED INDEX [UQ_{Table}_Code] ON [dbo].[{Table}]([Code] ASC) WHERE [Code] IS NOT NULL " +
                "WITH(PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, IGNORE_DUP_KEY = OFF, " +
                "DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON[PRIMARY]",
                sql);
        }

        [Fact]
        public void NonClusteredIndex_RendersPlainIndex_Golden()
        {
            var schema = UniqueSchema();

            var sql = SqlSnapshot.Normalize(SqlServerSchemaDialect.ColumnIndexSql(schema, schema["Timestamp"]));

            Assert.Equal(
                $"CREATE NONCLUSTERED INDEX [NCI_{Table}_Timestamp] ON [dbo].[{Table}]([Timestamp] DESC) " +
                "WITH(PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, SORT_IN_TEMPDB = OFF, " +
                "DROP_EXISTING = OFF, ONLINE = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON[PRIMARY]",
                sql);
        }

        [Fact]
        public void UniqueWithNonClusteredAndDesc_RendersOneUniqueDescendingIndex()
        {
            var schema = UniqueSchema();
            var sku = schema["Sku"];

            var sql = SqlSnapshot.Normalize(SqlServerSchemaDialect.ColumnIndexSql(schema, sku));

            Assert.StartsWith($"CREATE UNIQUE NONCLUSTERED INDEX [UQ_{Table}_Sku]", sql);
            Assert.Contains("([Sku] DESC) WHERE [Sku] IS NOT NULL", sql);
            Assert.True(sku.Unique);
            Assert.True(sku.Indexed);
            // One index per column: Code, Sku and the inherited Timestamp.
            Assert.Equal(3, schema.Columns.Count(c => c.HasColumnIndex));
        }

        [Fact]
        public void Scale_SetsTheDecimalScale_AndUnsetKeepsTheDefault()
        {
            var schema = UniqueSchema();

            Assert.Equal(6, schema["Weight"].Scale);
            Assert.Equal(14, schema["Weight"].Precision);
            Assert.Equal(5, schema["Amount"].Scale);
            Assert.Equal(15, schema["Amount"].Precision);
        }

        [Fact]
        public void ColumnWithoutIndex_ColumnIndexSqlThrows()
        {
            var schema = UniqueSchema();

            Assert.Throws<ArgumentException>(() => SqlServerSchemaDialect.ColumnIndexSql(schema, schema["ID"]));
        }

        [Fact]
        public void Unique_OnMaxLengthColumn_ThrowsAtSchemaBuild()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => new TableSchema(ObjectSchema.Get<TestUniqueMaxWidget>()));

            Assert.Contains("Body", ex.Message);
        }

        [Fact]
        public void PrimaryKey_OnSecondProperty_ThrowsAtSchemaBuild()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => new TableSchema(ObjectSchema.Get<TestDoublePrimaryKeyWidget>()));

            Assert.Contains("Code", ex.Message);
            Assert.Contains("ID", ex.Message);
        }

        [Fact]
        public void PrimaryKey_IsTheInheritedIdColumn()
        {
            var schema = new TableSchema(ObjectSchema.Get<TestWidget>());

            Assert.Equal("ID", schema.IdentityColumn.ColumnName);
            Assert.True(schema.IdentityColumn.PrimaryKey);
        }

        private static CatalogIndex Existing(string index, string column, bool unique, bool desc, bool filter) => new()
        {
            TableName = Table,
            IndexName = index,
            ColumnName = column,
            IsUnique = unique,
            IsDescending = desc,
            HasFilter = filter,
        };

        private static List<CatalogIndex> MatchingIndexes() =>
        [
            Existing($"UQ_{Table}_Code", "Code", unique: true, desc: false, filter: true),
            Existing($"UQ_{Table}_Sku", "Sku", unique: true, desc: true, filter: true),
            Existing($"NCI_{Table}_Timestamp", "Timestamp", unique: false, desc: true, filter: false),
        ];

        [Fact]
        public void Diff_EmptyDatabase_CreatesEveryIndex()
        {
            var (drop, create) = SchemaComparer.DiffColumnIndexes(UniqueSchema(), [], Dialect);

            Assert.Empty(drop);
            Assert.Equal(["Code", "Sku", "Timestamp"], create.Select(c => c.ColumnName).Order());
        }

        [Fact]
        public void Diff_MatchingDatabase_DoesNothing()
        {
            var (drop, create) = SchemaComparer.DiffColumnIndexes(UniqueSchema(), MatchingIndexes(), Dialect);

            Assert.Empty(drop);
            Assert.Empty(create);
        }

        [Fact]
        public void Diff_UniquenessChanged_DropsAndRecreates()
        {
            var existing = MatchingIndexes();
            existing[0].IsUnique = false;
            existing[0].HasFilter = false;

            var (drop, create) = SchemaComparer.DiffColumnIndexes(UniqueSchema(), existing, Dialect);

            Assert.Equal([$"UQ_{Table}_Code"], drop);
            Assert.Equal(["Code"], create.Select(c => c.ColumnName));
        }

        [Fact]
        public void Diff_StaleNucleusIndex_IsDropped()
        {
            var existing = MatchingIndexes();
            existing.Add(Existing($"NCI_{Table}_Removed", "Removed", unique: false, desc: false, filter: false));

            var (drop, create) = SchemaComparer.DiffColumnIndexes(UniqueSchema(), existing, Dialect);

            Assert.Equal([$"NCI_{Table}_Removed"], drop);
            Assert.Empty(create);
        }

        [Fact]
        public void Diff_OtherTable_IsIgnored()
        {
            var existing = MatchingIndexes();
            existing.Add(new CatalogIndex { TableName = "tbl_Other", IndexName = "NCI_tbl_Other_X", ColumnName = "X" });

            var (drop, create) = SchemaComparer.DiffColumnIndexes(UniqueSchema(), existing, Dialect);

            Assert.Empty(drop);
            Assert.Empty(create);
        }
    }
}
