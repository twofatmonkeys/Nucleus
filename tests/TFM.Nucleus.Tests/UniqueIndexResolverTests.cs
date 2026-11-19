using TFM.Nucleus.Data.PostgreSQL;
using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Data.SQLite;

namespace TFM.Nucleus.Tests
{
    /// <summary>A unique [TableIndex] on a table whose index names run past PostgreSQL's 63 bytes, so the engine stores them shortened.</summary>
    [DatabaseTable("tbl_AVeryLongTableNameThatPushesItsIndexNamesPastTheLimit")]
    [TableIndex("KeyPath", nameof(TemplateId), nameof(FilePath), Unique = true)]
    public class TestLongUniqueIndexedWidget : CoreObject<TestLongUniqueIndexedWidget>
    {
        [DatabaseColumn]
        public Guid? TemplateId { get => GetValue<Guid?>(); set => SetValue(value); }

        [DatabaseColumn]
        public string? FilePath { get => GetValue<string>(); set => SetValue(value); }
    }

    /// <summary>
    /// How a unique violation's index name becomes the entity and rule it broke, on both engines'
    /// naming, and what <see cref="UniqueViolationException"/> reports for each kind of index.
    /// </summary>
    public class UniqueIndexResolverTests
    {
        private const string UniqueTable = "tbl_TFM_Nucleus_Tests_TestUniqueIndexedWidget";
        private const string LongTable = "tbl_AVeryLongTableNameThatPushesItsIndexNamesPastTheLimit";

        public static TheoryData<string> Engines() => new("sqlserver", "postgres", "sqlite");

        private static ISchemaDialect Dialect(string engine) => engine switch
        {
            "sqlserver" => new SqlServerSchemaDialect(null),
            "postgres" => new PostgresSchemaDialect("public", null),
            "sqlite" => new SqliteSchemaDialect(),
            _ => throw new ArgumentOutOfRangeException(nameof(engine)),
        };

        private static List<TableSchema> Tables() =>
        [
            new(ObjectSchema.Get<TestIndexedWidget>()),
            new(ObjectSchema.Get<TestUniqueIndexedWidget>()),
            new(ObjectSchema.Get<TestLongUniqueIndexedWidget>()),
        ];

        private static ResolvedUniqueIndex? Resolve(string engine, string logicalName) =>
            UniqueIndexResolver.Resolve(Tables(), Dialect(engine), Dialect(engine).PhysicalName(logicalName));

        [Theory]
        [MemberData(nameof(Engines))]
        public void CompositeIndex_NamesTheEntity_TheDeclaredIndex_AndEveryKey(string engine)
        {
            var resolved = Resolve(engine, $"NIX_{UniqueTable}_CodeQuantity");

            Assert.NotNull(resolved);
            Assert.Equal(typeof(TestUniqueIndexedWidget), resolved!.EntityType);
            Assert.Equal($"NIX_{UniqueTable}_CodeQuantity", resolved.IndexName);
            Assert.Equal("CodeQuantity", resolved.DeclaredIndexName);
            Assert.Equal(["Code", "Quantity"], resolved.PropertyNames);
            Assert.Equal(["Code", "Quantity"], resolved.ColumnNames);
        }

        [Theory]
        [MemberData(nameof(Engines))]
        public void CompositeIndex_WhoseStoredNameIsShortened_IsStillFound(string engine)
        {
            var logical = $"NIX_{LongTable}_KeyPath";
            if (engine == "postgres")
                Assert.NotEqual(logical, Dialect(engine).PhysicalName(logical));

            var resolved = Resolve(engine, logical);

            Assert.NotNull(resolved);
            Assert.Equal(typeof(TestLongUniqueIndexedWidget), resolved!.EntityType);
            Assert.Equal(logical, resolved.IndexName);
            Assert.Equal("KeyPath", resolved.DeclaredIndexName);
            Assert.Equal(["TemplateId", "FilePath"], resolved.PropertyNames);
        }

        [Theory]
        [MemberData(nameof(Engines))]
        public void ColumnIndex_GivesOneEntryLists_AndNoDeclaredName(string engine)
        {
            var resolved = Resolve(engine, $"UQ_{UniqueTable}_Code");

            Assert.NotNull(resolved);
            Assert.Equal(typeof(TestUniqueIndexedWidget), resolved!.EntityType);
            Assert.Equal($"UQ_{UniqueTable}_Code", resolved.IndexName);
            Assert.Null(resolved.DeclaredIndexName);
            Assert.Equal(["Code"], resolved.PropertyNames);
            Assert.Equal(["Code"], resolved.ColumnNames);
        }

        [Theory]
        [MemberData(nameof(Engines))]
        public void PlainTableIndex_UnknownName_OrNoName_IsNotResolved(string engine)
        {
            Assert.Null(Resolve(engine, "NIX_tbl_TFM_Nucleus_Tests_TestIndexedWidget_NameQuantity"));
            Assert.Null(Resolve(engine, $"NIX_{UniqueTable}_Missing"));
            Assert.Null(Resolve(engine, "PK_tbl_Anything"));
            Assert.Null(UniqueIndexResolver.Resolve(Tables(), Dialect(engine), null));
        }

        [Fact]
        public void ColumnIndexException_FillsTheSingleNames_AndOneEntryLists()
        {
            var violation = new UniqueViolationException("m", "UQ_x", typeof(TestRenamedWidget), "Code", "item_code", new Exception());

            Assert.Equal(["Code"], violation.PropertyNames);
            Assert.Equal(["item_code"], violation.ColumnNames);
            Assert.Null(violation.DeclaredIndexName);
            Assert.True(violation.Is<TestRenamedWidget>("Code"));
            Assert.False(violation.IsIndex<TestRenamedWidget>("Code"));
        }

        [Fact]
        public void UnmanagedIndexException_HasEmptyLists()
        {
            var violation = new UniqueViolationException("m", "PK_x", null, null, null, new Exception());

            Assert.Empty(violation.PropertyNames);
            Assert.Empty(violation.ColumnNames);
            Assert.Null(violation.DeclaredIndexName);
        }

        [Fact]
        public void TableIndexException_MatchesOnlyItsDeclaredName_OnItsOwnType()
        {
            var violation = new UniqueViolationException("m", $"NIX_{UniqueTable}_CodeQuantity", typeof(TestUniqueIndexedWidget),
                "CodeQuantity", ["Code", "Quantity"], ["Code", "Quantity"], new Exception());

            Assert.Null(violation.PropertyName);
            Assert.Null(violation.ColumnName);
            Assert.Equal(["Code", "Quantity"], violation.PropertyNames);
            Assert.True(violation.IsIndex<TestUniqueIndexedWidget>("CodeQuantity"));
            Assert.True(violation.IsIndex<TestUniqueIndexedWidget>("codequantity"));
            Assert.False(violation.IsIndex<TestUniqueIndexedWidget>("NameCode"));
            Assert.False(violation.IsIndex<TestIndexedWidget>("CodeQuantity"));
            Assert.False(violation.Is<TestUniqueIndexedWidget>("Code"));
        }
    }
}
