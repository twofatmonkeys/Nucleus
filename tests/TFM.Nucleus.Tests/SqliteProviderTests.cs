using Microsoft.Data.Sqlite;
using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Data.SQLite;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// The forms SQLite stores values in: each one is fixed, reads back to the same value, and
    /// for dates and times sorts as text in the order of the instants.
    /// </summary>
    public class SqliteValuesTests
    {
        private static ColumnSchema Column(string property) => QueryGoldenCorpus.TableOf(typeof(TestAllTypesWidget))[property];

        [Fact]
        public void Guid_IsLowerCaseText_AndReadsBack()
        {
            var id = Guid.Parse("8C2F1B3A-4D5E-4F60-9A7B-1C2D3E4F5A6B");

            var stored = SqliteValues.ToDb(id, null);

            Assert.Equal("8c2f1b3a-4d5e-4f60-9a7b-1c2d3e4f5a6b", stored);
            Assert.Equal(id, SqliteValues.FromDb(stored, Column("Reference")));
        }

        [Fact]
        public void DateTimeOffset_IsUtcText_WithSevenFractionalDigits()
        {
            var at = new DateTimeOffset(2026, 11, 19, 14, 30, 5, TimeSpan.FromHours(10)).AddTicks(1234567);

            var stored = SqliteValues.ToDb(at, Column("At"));

            Assert.Equal("2026-11-19 04:30:05.1234567+00:00", stored);
            var read = (DateTimeOffset)SqliteValues.FromDb(stored, Column("At"))!;
            Assert.Equal(at, read);
            Assert.Equal(TimeSpan.Zero, read.Offset);
        }

        [Fact]
        public void DateTime_KeepsItsWallTime_AndDateKeepsItsDay()
        {
            var when = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local).AddTicks(7);

            Assert.Equal("2026-01-02 03:04:05.0000007", SqliteValues.ToDb(when, Column("When")));
            Assert.Equal("2026-01-02", SqliteValues.ToDb(when, Column("Day")));
            Assert.Equal(DateTime.SpecifyKind(when, DateTimeKind.Unspecified), SqliteValues.FromDb("2026-01-02 03:04:05.0000007", Column("When")));
            Assert.Equal(new DateTime(2026, 1, 2), SqliteValues.FromDb("2026-01-02", Column("Day")));
        }

        [Fact]
        public void DateTimeText_SortsInTheOrderOfTheInstants()
        {
            DateTimeOffset[] instants =
            [
                new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(1),
                new(2026, 1, 1, 0, 0, 1, TimeSpan.Zero),
                new(2025, 12, 31, 23, 0, 0, TimeSpan.FromHours(-2)),
                new(2026, 1, 1, 9, 0, 0, TimeSpan.FromHours(10)),
            ];

            var byText = instants.OrderBy(i => (string)SqliteValues.ToDb(i, null)!, StringComparer.Ordinal).ToList();

            Assert.Equal(instants.OrderBy(i => i.UtcDateTime), byText);
        }

        [Fact]
        public void Decimal_IsBoundAsAReal_AndReadBackRoundedToItsScale()
        {
            var money = Column("Money");

            Assert.Equal(12.34d, SqliteValues.ToDb(12.34m, money));
            Assert.Equal(0.1m, SqliteValues.FromDb(0.1d, money));
            Assert.Equal(1234.57m, SqliteValues.FromDb(1234.5678d, money));
            Assert.Equal(12m, SqliteValues.FromDb(12L, money));
        }

        [Fact]
        public void BoolAndEnum_AreIntegers()
        {
            Assert.Equal(1L, SqliteValues.ToDb(true, null));
            Assert.Equal(0L, SqliteValues.ToDb(false, null));
            Assert.Equal(true, SqliteValues.FromDb(1L, Column("Flag")));
            Assert.Equal(2L, SqliteValues.ToDb(DayOfWeek.Tuesday, null));
            Assert.Equal(2, SqliteValues.FromDb(2L, Column("Kind")));
        }

        [Fact]
        public void Null_IsBoundAsDbNull()
        {
            Assert.Same(DBNull.Value, SqliteValues.ToDb(null, null));
            Assert.Same(DBNull.Value, SqliteValues.FromDb(DBNull.Value, Column("At")));
        }

        [Fact]
        public void ForeignText_ThatIsNoNucleusForm_FailsWithTheValue()
        {
            var ex = Assert.Throws<FormatException>(() => SqliteValues.FromDb("next tuesday", Column("At")));
            Assert.Contains("next tuesday", ex.Message);
        }
    }

    /// <summary>
    /// The functions registered on every connection, run by a real SQLite: pattern terms read
    /// their escapes, fold case across Unicode, and the server functions write the bound forms.
    /// </summary>
    public class SqliteFunctionsTests
    {
        [Theory]
        [InlineData("Hello World", "%world%", false, true)]
        [InlineData("Hello World", "%world%", true, false)]
        [InlineData("Hello World", "%World%", true, true)]
        [InlineData("ÉCOLE", "%école%", false, true)]
        [InlineData("straße", "STRASSE", false, false)]
        [InlineData("abc", "a_c", false, true)]
        [InlineData("ac", "a_c", false, false)]
        [InlineData("100%", "100\\%", false, true)]
        [InlineData("1000", "100\\%", false, false)]
        [InlineData("a_b", "a\\_b", false, true)]
        [InlineData("axb", "a\\_b", false, false)]
        [InlineData("a\\b", "a\\\\b", false, true)]
        [InlineData("[x]", "\\[x]", false, true)]
        [InlineData("", "%", false, true)]
        [InlineData("abcabc", "%abc", false, true)]
        [InlineData("aXbXc", "a%b%c", false, true)]
        [InlineData("aXbXd", "a%b%c", false, false)]
        public void IsLike_MatchesAsLikeWithTheNucleusEscape(string value, string pattern, bool caseSensitive, bool expected) =>
            Assert.Equal(expected, SqliteFunctions.IsLike(value, pattern, caseSensitive));

        private static SqliteConnection OpenMemory()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            SqliteFunctions.Register(connection);
            connection.Open();
            return connection;
        }

        private static object? Scalar(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return command.ExecuteScalar();
        }

        [Fact]
        public void RegisteredFunctions_RunInSqlite()
        {
            using var connection = OpenMemory();

            Assert.Equal(1L, Scalar(connection, $"SELECT {SqliteFunctions.Like}('Ünïcode', '%' || @p || '%')", ("@p", "ÜNÏ")));
            Assert.Equal(0L, Scalar(connection, $"SELECT {SqliteFunctions.LikeCaseSensitive}('Ünïcode', '%' || @p || '%')", ("@p", "ÜNÏ")));
            Assert.Equal("ünïcode", Scalar(connection, $"SELECT {SqliteFunctions.Lower}('ÜNÏCODE')"));
            Assert.Equal(1L, Scalar(connection, $"SELECT {SqliteFunctions.Contains}('100% Wool', '% wool')"));
            Assert.Same(DBNull.Value, Scalar(connection, $"SELECT {SqliteFunctions.Like}(NULL, '%')"));
        }

        [Fact]
        public void ServerFunctions_WriteTheBoundForms()
        {
            using var connection = OpenMemory();

            var now = (string)Scalar(connection, $"SELECT {SqliteFunctions.NowOffset}()")!;
            var guid = (string)Scalar(connection, $"SELECT {SqliteFunctions.NewGuid}()")!;

            Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{7}\+00:00$", now);
            Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", guid);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{7}$", (string)Scalar(connection, $"SELECT {SqliteFunctions.NowUtc}()")!);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{7}$", (string)Scalar(connection, $"SELECT {SqliteFunctions.NowLocal}()")!);
        }

        [Fact]
        public void TheBundledSqlite_IsNewEnough()
        {
            using var connection = OpenMemory();

            var version = Version.Parse((string)Scalar(connection, "SELECT sqlite_version()")!);

            Assert.True(version >= SqliteCatalogReader.MinimumVersion, $"SQLite {version} is older than {SqliteCatalogReader.MinimumVersion}.");
        }
    }

    /// <summary>
    /// The declared types: their facets survive a round trip through SQLite's catalog, and their
    /// affinity leaves every stored form as it was bound.
    /// </summary>
    public class SqliteTypesTests
    {
        [Theory]
        [InlineData("VARCHAR(255)", "VARCHAR", 255, 0, 0)]
        [InlineData("DECIMAL(18,2)", "DECIMAL", 0, 18, 2)]
        [InlineData("decimal( 15 , 5 )", "DECIMAL", 0, 15, 5)]
        [InlineData("UUID", "UUID", 0, 0, 0)]
        [InlineData("", "", 0, 0, 0)]
        public void ParseDeclaredType_SplitsTheNameAndFacets(string declared, string name, int length, int precision, int scale) =>
            Assert.Equal((name, length, precision, scale), SqliteTypes.ParseDeclaredType(declared));

        [Fact]
        public void EveryColumn_MatchesWhatTheCatalogReadsBack()
        {
            var table = QueryGoldenCorpus.TableOf(typeof(TestAllTypesWidget));
            using var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            using (var create = connection.CreateCommand())
            {
                create.CommandText = new SqliteSchemaDialect().CreateTable(table)[0].Text;
                create.ExecuteNonQuery();
            }
            using var read = connection.CreateCommand();
            read.CommandText = SqliteCatalogReader.ColumnsQuery(table.TableName);
            using var reader = read.ExecuteReader();
            var catalog = new Dictionary<string, CatalogColumn>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                var column = SqliteCatalogReader.ReadColumn(reader);
                catalog[column.Name!] = column;
            }

            foreach (var column in table.Columns)
                Assert.True(SqliteTypes.Matches(column, catalog[column.ColumnName]), $"{column.ColumnName} ({column.Intent}) does not match {catalog[column.ColumnName].TypeName}.");
        }

        [Fact]
        public void Affinity_KeepsEveryBoundFormAsItWasBound()
        {
            var table = QueryGoldenCorpus.TableOf(typeof(TestAllTypesWidget));
            using var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            using (var create = connection.CreateCommand())
            {
                create.CommandText = new SqliteSchemaDialect().CreateTable(table)[0].Text;
                create.ExecuteNonQuery();
            }
            (string Column, object Value, string StoredType)[] samples =
            [
                ("Reference", Guid.Parse("12345678-0000-0000-0000-000000000001"), "text"),
                ("At", DateTimeOffset.UnixEpoch, "text"),
                ("When", new DateTime(2026, 1, 1), "text"),
                ("Day", new DateTime(2026, 1, 1), "text"),
                ("Flag", true, "integer"),
                ("Money", 12.5m, "real"),
                ("Count", 5, "integer"),
                ("Ansi", "123", "text"),
            ];
            foreach (var (name, value, storedType) in samples)
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = $"INSERT INTO \"{table.TableName}\" (\"ID\", \"{name}\") VALUES (@id, @v); SELECT typeof(\"{name}\") FROM \"{table.TableName}\" WHERE \"ID\" = @id";
                insert.Parameters.AddWithValue("@id", SqliteValues.ToDb(Guid.NewGuid(), null));
                insert.Parameters.AddWithValue("@v", SqliteValues.ToDb(value, table[name]));
                Assert.Equal(storedType, insert.ExecuteScalar());
            }
        }
    }

    /// <summary>
    /// An engine without included columns stores a [TableIndex] over its keys alone, and the
    /// schema check takes that as current; an engine with them still wants them.
    /// </summary>
    public class IncludedColumnsComparisonTests
    {
        private static IReadOnlyList<CatalogIndex> KeysOnly(TableSchema table, TableIndexSchema index) =>
        [
            .. index.Keys.Select((k, i) => new CatalogIndex
            {
                TableName = table.TableName,
                IndexName = IndexNaming.TableIndexName(table, index),
                ColumnName = k.ColumnName,
                IsUnique = index.Unique,
                HasFilter = index.Unique,
                KeyOrdinal = i + 1,
            }),
        ];

        [Fact]
        public void AKeysOnlyIndex_IsCurrent_WhereTheEngineHasNoIncludedColumns()
        {
            var table = QueryGoldenCorpus.TableOf(typeof(TestUniqueIndexedWidget));
            var index = table.TableIndexes.Single(i => i.Name == "NameCode");
            Assert.NotEmpty(index.Include);

            var (drop, create) = SchemaComparer.DiffTableIndexes(table, KeysOnly(table, index), new SqliteSchemaDialect());

            Assert.Empty(drop);
            Assert.DoesNotContain(index, create);
        }

        [Fact]
        public void AKeysOnlyIndex_IsStale_WhereTheEngineHasIncludedColumns()
        {
            var table = QueryGoldenCorpus.TableOf(typeof(TestUniqueIndexedWidget));
            var index = table.TableIndexes.Single(i => i.Name == "NameCode");

            var (drop, create) = SchemaComparer.DiffTableIndexes(table, KeysOnly(table, index), new SqlServerSchemaDialect(null));

            Assert.Contains(IndexNaming.TableIndexName(table, index), drop);
            Assert.Contains(index, create);
        }

        [Fact]
        public void AnIncludedColumnFoundAnyway_MakesTheIndexStale_WhereTheEngineHasNone()
        {
            var table = QueryGoldenCorpus.TableOf(typeof(TestUniqueIndexedWidget));
            var index = table.TableIndexes.Single(i => i.Name == "NameCode");
            List<CatalogIndex> rows =
            [
                .. KeysOnly(table, index),
                new CatalogIndex { TableName = table.TableName, IndexName = IndexNaming.TableIndexName(table, index), ColumnName = "Notes", IsUnique = true, HasFilter = true, IsIncluded = true },
            ];

            var (drop, _) = SchemaComparer.DiffTableIndexes(table, rows, new SqliteSchemaDialect());

            Assert.Contains(IndexNaming.TableIndexName(table, index), drop);
        }
    }

    /// <summary>
    /// SQLite names a unique violation by its columns; the provider reads them, and a real
    /// violation from the driver carries the codes it looks for.
    /// </summary>
    public class SqliteErrorsTests
    {
        [Fact]
        public void Parse_ReadsTheTableAndColumns_SplittingAtTheLastDot()
        {
            var target = SqliteErrors.Parse("SQLite Error 19: 'UNIQUE constraint failed: tbl_TFM.Nucleus.Widget.Code, tbl_TFM.Nucleus.Widget.Quantity'.", false);

            Assert.Equal("tbl_TFM.Nucleus.Widget", target.TableName);
            Assert.Equal(["Code", "Quantity"], target.Columns);
            Assert.Null(target.IndexName);
        }

        [Fact]
        public void Parse_ReadsAnIndexNamedByTheEngine()
        {
            var target = SqliteErrors.Parse("SQLite Error 19: 'UNIQUE constraint failed: index 'NIX_t_Lower''.", false);

            Assert.Equal("NIX_t_Lower", target.IndexName);
        }

        [Fact]
        public void ARealViolation_IsReadWithItsColumns()
        {
            using var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE \"tbl_A.B\" (\"ID\" TEXT PRIMARY KEY, \"Code\" TEXT, \"Qty\" INTEGER);" +
                "CREATE UNIQUE INDEX \"NIX_x\" ON \"tbl_A.B\" (\"Code\", \"Qty\") WHERE \"Code\" IS NOT NULL AND \"Qty\" IS NOT NULL;" +
                "INSERT INTO \"tbl_A.B\" VALUES ('1', 'a', 1);";
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO \"tbl_A.B\" VALUES ('2', 'a', 1)";

            var ex = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());

            Assert.True(SqliteErrors.TryGetUniqueViolation(new Exception("wrapped", ex), out var target));
            Assert.Equal("tbl_A.B", target.TableName);
            Assert.Equal(["Code", "Qty"], target.Columns);
            Assert.False(target.IsPrimaryKey);

            command.CommandText = "INSERT INTO \"tbl_A.B\" VALUES ('1', 'b', 2)";
            var pk = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
            Assert.True(SqliteErrors.TryGetUniqueViolation(pk, out var key));
            Assert.True(key.IsPrimaryKey);
        }

        [Fact]
        public void APassword_IsRefused_RatherThanIgnored()
        {
            var config = new SqliteConfiguration { Database = "nucleus.db", Password = "secret" };

            var ex = Assert.Throws<SqliteDataProviderException>(() => config.BuildConnection());

            Assert.Equal(DataProviderErrorKind.NotConfigured, ex.Kind);
        }

        [Theory]
        [InlineData(":memory:")]
        [InlineData("file:nucleus?mode=memory")]
        public void AnInMemoryDatabase_IsRefused(string path)
        {
            var config = new SqliteConfiguration { Database = path };

            Assert.Throws<SqliteDataProviderException>(() => config.FilePath);
        }
    }
}
