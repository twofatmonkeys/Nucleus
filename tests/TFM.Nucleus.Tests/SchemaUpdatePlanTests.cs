using TFM.Nucleus.Data.PostgreSQL;
using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Data.SQLite;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// How a compared table is brought in line: a table that only lacks columns has them added
    /// in place, a column of the wrong type means a rebuild. Each case runs against the catalog
    /// shape each provider reports, through that provider's own dialect.
    /// </summary>
    public class SchemaUpdatePlanTests
    {
        public static TheoryData<string> Providers() => new("sqlserver", "postgres", "sqlite");

        private static ISchemaDialect Dialect(string provider) => provider switch
        {
            "sqlserver" => new SqlServerSchemaDialect(null),
            "postgres" => new PostgresSchemaDialect("public", null),
            "sqlite" => new SqliteSchemaDialect(),
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };

        private static TableSchema Growing() => QueryGoldenCorpus.TableOf(typeof(TestGrowingWidget));

        [Theory]
        [MemberData(nameof(Providers))]
        public void ACompleteTable_IsCurrent(string provider)
        {
            var table = Growing();

            Assert.True(Compare(provider, table, SchemaCatalog.Current(provider, table)));
            Assert.True(table.IsCurrent);
        }

        [Theory]
        [MemberData(nameof(Providers))]
        public void MissingColumnsOnly_AreAddedInPlace(string provider)
        {
            var table = Growing();
            var catalog = SchemaCatalog.Current(provider, table).Without(table.TableName, "Key", "Extra");

            Assert.False(Compare(provider, table, catalog));

            var plan = SchemaComparer.PlanUpdate(table);
            Assert.Equal(TableUpdateKind.AddColumns, plan.Kind);
            Assert.Equal(["Key", "Extra"], plan.Columns.Select(c => c.ColumnName));
            Assert.Empty(plan.SoftDeleteColumns);
        }

        [Theory]
        [MemberData(nameof(Providers))]
        public void ATypeMismatch_Rebuilds(string provider)
        {
            var table = Growing();
            var catalog = SchemaCatalog.Current(provider, table).Retyped(table.TableName, "Name", "integer");

            Assert.False(Compare(provider, table, catalog));

            Assert.Equal(TableUpdateKind.Rebuild, SchemaComparer.PlanUpdate(table).Kind);
        }

        [Theory]
        [MemberData(nameof(Providers))]
        public void AMissingColumnAndATypeMismatch_Rebuilds(string provider)
        {
            var table = Growing();
            var catalog = SchemaCatalog.Current(provider, table).Without(table.TableName, "Key").Retyped(table.TableName, "Name", "integer");

            Assert.False(Compare(provider, table, catalog));

            Assert.Equal(TableUpdateKind.Rebuild, SchemaComparer.PlanUpdate(table).Kind);
        }

        [Theory]
        [MemberData(nameof(Providers))]
        public void MissingSoftDeleteColumns_MakeTheTableNotCurrent_AndAreAdded(string provider)
        {
            var table = Growing();
            var catalog = SchemaCatalog.Current(provider, table).Without(table.TableName, SoftDelete.IsDeletedColumn, SoftDelete.DeletedAtColumn);

            Assert.False(Compare(provider, table, catalog));
            // The update skips current tables, so a table lacking only these was never repaired.
            Assert.False(table.IsCurrent);

            var plan = SchemaComparer.PlanUpdate(table);
            Assert.Equal(TableUpdateKind.AddColumns, plan.Kind);
            Assert.Empty(plan.Columns);
            Assert.Equal([SoftDelete.IsDeletedColumn, SoftDelete.DeletedAtColumn], plan.SoftDeleteColumns);
        }

        [Theory]
        [MemberData(nameof(Providers))]
        public void OneMissingSoftDeleteColumn_IsTheOnlyOneAdded(string provider)
        {
            var table = Growing();
            var catalog = SchemaCatalog.Current(provider, table).Without(table.TableName, SoftDelete.DeletedAtColumn);

            Assert.False(Compare(provider, table, catalog));

            var plan = SchemaComparer.PlanUpdate(table);
            Assert.Equal(TableUpdateKind.AddColumns, plan.Kind);
            Assert.Equal([SoftDelete.DeletedAtColumn], plan.SoftDeleteColumns);
        }

        [Theory]
        [MemberData(nameof(Providers))]
        public void AMissingIdColumn_Rebuilds(string provider)
        {
            var table = Growing();
            var catalog = SchemaCatalog.Current(provider, table).Without(table.TableName, "ID");

            Assert.False(Compare(provider, table, catalog));

            Assert.Equal(TableUpdateKind.Rebuild, SchemaComparer.PlanUpdate(table).Kind);
        }

        [Theory]
        [MemberData(nameof(Providers))]
        public void AMissingTable_IsCreated(string provider)
        {
            var table = Growing();

            Assert.False(Compare(provider, table, new CatalogSnapshot([], [], [])));

            Assert.Equal(TableUpdateKind.Create, SchemaComparer.PlanUpdate(table).Kind);
        }

        [Theory]
        [MemberData(nameof(Providers))]
        public void ACompareAfterTheColumnsWereAdded_ForgetsTheMissingSoftDeleteColumns(string provider)
        {
            var table = Growing();
            Compare(provider, table, SchemaCatalog.Current(provider, table).Without(table.TableName, SoftDelete.IsDeletedColumn));

            Assert.True(Compare(provider, table, SchemaCatalog.Current(provider, table)));
            Assert.True(table.IsCurrent);
        }

        [Fact]
        public void SqlServer_DeclinesToAddAFileStreamColumn()
        {
            var table = Growing();
            var dialect = new SqlServerSchemaDialect(null);

            Assert.Null(dialect.AddColumns(table, [table["Key"], table["Blob"]], []));
            Assert.Single(dialect.AddColumns(table, [table["Key"]], [])!);
        }

        [Fact]
        public void Postgres_AddsAnExternallyStoredColumn()
        {
            var table = Growing();

            Assert.Single(new PostgresSchemaDialect("public", null).AddColumns(table, [table["Blob"]], [])!);
        }

        [Theory]
        [MemberData(nameof(Providers))]
        public void AddColumns_RefusesAnEmptyList(string provider)
        {
            Assert.Throws<ArgumentException>(() => Dialect(provider).AddColumns(Growing(), [], []));
        }

        [Fact]
        public void SchemaCommandTimeout_DefaultsToFifteenMinutes_AllowsNoLimit_AndRefusesANegative()
        {
            var options = new SQLConfiguration();

            Assert.Equal(900, options.SchemaCommandTimeoutSeconds);
            options.SchemaCommandTimeoutSeconds = 0;
            Assert.Equal(0, options.SchemaCommandTimeoutSeconds);
            Assert.Throws<ArgumentOutOfRangeException>(() => options.SchemaCommandTimeoutSeconds = -1);
        }

        private static bool Compare(string provider, TableSchema table, CatalogSnapshot catalog) =>
            SchemaComparer.CompareTables([table], catalog, Dialect(provider), null);
    }

    /// <summary>
    /// What the schema manager runs for a table that needs changing, against an in-memory
    /// catalog: the statements on its transaction and those it runs outside one.
    /// </summary>
    public class SchemaManagerUpdateTests
    {
        private static TableSchema Growing() => QueryGoldenCorpus.TableOf(typeof(TestGrowingWidget));

        [Fact]
        public void MissingColumns_AreAddedWithoutARebuild_AndTheirIndexIsCreated()
        {
            var table = Growing();
            var store = new RecordingSchemaStore(SchemaCatalog.Current("sqlserver", table).Without(table.TableName, "Key", "Extra"));

            Assert.Equal(EDataSchemaStatus.Ok, store.Manager().Initialize());

            var statement = Assert.Single(store.InTransaction);
            Assert.Equal(SqlServerSchemaDialect.AddColumnsSql(table, [table["Key"], table["Extra"]], []), statement);
            Assert.Contains(store.OutsideTransaction, s => s.StartsWith("CREATE NONCLUSTERED INDEX [NCI_tbl_TFM_Nucleus_Tests_TestGrowingWidget_Key]", StringComparison.Ordinal));
        }

        [Fact]
        public void MissingSoftDeleteColumns_AreAdded()
        {
            var table = Growing();
            var store = new RecordingSchemaStore(SchemaCatalog.Current("sqlserver", table).Without(table.TableName, SoftDelete.IsDeletedColumn, SoftDelete.DeletedAtColumn));

            Assert.Equal(EDataSchemaStatus.Ok, store.Manager().Initialize());

            Assert.Equal(SqlServerSchemaDialect.AddColumnsSql(table, [], [SoftDelete.IsDeletedColumn, SoftDelete.DeletedAtColumn]), Assert.Single(store.InTransaction));
        }

        [Fact]
        public void AMissingTimestamp_IsAddedThenBackfilled()
        {
            var table = Growing();
            var store = new RecordingSchemaStore(SchemaCatalog.Current("sqlserver", table).Without(table.TableName, TableSchema.TimestampColumn));

            Assert.Equal(EDataSchemaStatus.Ok, store.Manager().Initialize());

            Assert.Equal(
                [SqlServerSchemaDialect.AddColumnsSql(table, [table[TableSchema.TimestampColumn]], []), SqlServerSchemaDialect.BackfillTimestampSql(table.TableName)],
                store.InTransaction);
        }

        [Fact]
        public void ATypeMismatch_Rebuilds()
        {
            var table = Growing();
            var store = new RecordingSchemaStore(SchemaCatalog.Current("sqlserver", table).Retyped(table.TableName, "Name", "int"));

            Assert.Equal(EDataSchemaStatus.Ok, store.Manager().Initialize());

            Assert.StartsWith("EXEC sp_rename 'tbl_TFM_Nucleus_Tests_TestGrowingWidget'", store.InTransaction[0], StringComparison.Ordinal);
            Assert.DoesNotContain(store.InTransaction, s => s.StartsWith("ALTER TABLE [dbo].[tbl_TFM_Nucleus_Tests_TestGrowingWidget] ADD [", StringComparison.Ordinal));
            Assert.Contains(store.InTransaction, s => s.StartsWith("INSERT INTO [dbo].[tbl_TFM_Nucleus_Tests_TestGrowingWidget](", StringComparison.Ordinal));
        }

        [Fact]
        public void AColumnTheDialectCannotAdd_Rebuilds()
        {
            var table = Growing();
            var store = new RecordingSchemaStore(SchemaCatalog.Current("sqlserver", table).Without(table.TableName, "Blob"));

            Assert.Equal(EDataSchemaStatus.Ok, store.Manager().Initialize());

            Assert.StartsWith("EXEC sp_rename ", store.InTransaction[0], StringComparison.Ordinal);
        }
    }

    /// <summary>A catalog as a provider would report a table that matches its schema, and edits of it.</summary>
    internal static class SchemaCatalog
    {
        public static CatalogSnapshot Current(string provider, TableSchema table)
        {
            List<CatalogColumn> columns = [.. table.Columns.Select(c => Column(provider, table.TableName, c))];
            // SQLite tables have no cluster ID: the rowid keeps their insertion order.
            if (provider != "sqlite")
                columns.Add(new CatalogColumn { TableName = table.TableName, Name = TableSchema.ClusterIdColumn, TypeName = provider == "sqlserver" ? "int" : "bigint" });
            if (table.IsSoftDeletable)
            {
                var (isDeleted, deletedAt) = provider switch
                {
                    "sqlserver" => ("bit", "datetimeoffset"),
                    "postgres" => ("boolean", "timestamp with time zone"),
                    "sqlite" => ("BOOLEAN", "DATETIMEOFFSET"),
                    _ => throw new ArgumentOutOfRangeException(nameof(provider)),
                };
                columns.Add(new CatalogColumn { TableName = table.TableName, Name = SoftDelete.IsDeletedColumn, TypeName = isDeleted, IsNullable = false });
                columns.Add(new CatalogColumn { TableName = table.TableName, Name = SoftDelete.DeletedAtColumn, TypeName = deletedAt });
            }
            List<CatalogIndex> indexes = [.. table.IndexedColumns.Select(c => new CatalogIndex
            {
                TableName = table.TableName,
                IndexName = IndexNaming.ColumnIndexName(table, c),
                ColumnName = c.ColumnName,
                IsUnique = c.Unique,
                HasFilter = c.Unique,
                IsDescending = c.IndexSortDescending,
            })];
            return new CatalogSnapshot([table.TableName], columns, indexes);
        }

        /// <summary>The catalog with the named columns, and any index on them, gone from the table.</summary>
        public static CatalogSnapshot Without(this CatalogSnapshot catalog, string table, params string[] columns) => catalog with
        {
            Columns = [.. catalog.Columns.Where(c => !(c.TableName == table && columns.Contains(c.Name)))],
            Indexes = [.. catalog.Indexes.Where(i => !(i.TableName == table && columns.Contains(i.ColumnName)))],
        };

        /// <summary>The catalog with one column of the table reported as another type.</summary>
        public static CatalogSnapshot Retyped(this CatalogSnapshot catalog, string table, string column, string typeName) => catalog with
        {
            Columns = [.. catalog.Columns.Select(c => c.TableName == table && c.Name == column
                ? new CatalogColumn { TableName = c.TableName, Name = c.Name, TypeName = typeName }
                : c)],
        };

        private static CatalogColumn Column(string provider, string table, ColumnSchema column) => new()
        {
            TableName = table,
            Name = column.ColumnName,
            TypeName = provider switch
            {
                "sqlserver" => SqlServerTypeMapper.SqlType(column).ToString().ToLowerInvariant(),
                "postgres" => PostgresTypes.CatalogTypeName(column),
                "sqlite" => SqliteTypes.CatalogTypeName(column),
                _ => throw new ArgumentOutOfRangeException(nameof(provider)),
            },
            DataLength = column.DataLength ?? 0,
            Precision = column.Precision ?? 0,
            Scale = column.Scale ?? 0,
        };
    }
}
