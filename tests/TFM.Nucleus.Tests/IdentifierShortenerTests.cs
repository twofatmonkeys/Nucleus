using System.Text;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// A provider with an identifier limit must write and look for the same name every time, or
    /// its schema check would rebuild the object on every start.
    /// </summary>
    public class IdentifierShortenerTests
    {
        [Fact]
        public void NameThatFits_IsUnchanged()
        {
            var name = new string('a', 63);

            Assert.Same(name, IdentifierShortener.Shorten(name, 63));
        }

        [Fact]
        public void LongName_FitsTheLimit_KeepsItsStartAndEnd_AndIsStable()
        {
            const string name = "NCI_tbl_TFM_Nucleus_PortlyPrimate_PrimateGiftShopVisit_SessionId"; // 64 bytes

            var shortened = IdentifierShortener.Shorten(name, 63);

            Assert.Equal(63, Encoding.UTF8.GetByteCount(shortened));
            Assert.StartsWith("NCI_tbl_TFM_Nucleus_PortlyPrim_", shortened);
            Assert.EndsWith("_GiftShopVisit_SessionId", shortened);
            Assert.Equal(shortened, IdentifierShortener.Shorten(name, 63));
        }

        [Fact]
        public void LongNames_SharingTheirEnds_StayDistinct()
        {
            var a = IdentifierShortener.Shorten($"NCI_{new string('x', 40)}_Middle1_{new string('y', 30)}", 63);
            var b = IdentifierShortener.Shorten($"NCI_{new string('x', 40)}_Middle2_{new string('y', 30)}", 63);

            Assert.NotEqual(a, b);
        }

        [Fact]
        public void MultiByteCharacters_AreNeverSplit()
        {
            var name = "UQ_" + string.Concat(Enumerable.Repeat("é", 40)) + "_Column";

            var shortened = IdentifierShortener.Shorten(name, 63);

            Assert.True(Encoding.UTF8.GetByteCount(shortened) <= 63);
            Assert.DoesNotContain('�', Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(shortened)));
        }

        [Fact]
        public void PostgresDialect_UsesTheShortenedNameForIndexes()
        {
            var table = new TableSchema(ObjectSchema.Get<TestLongNamedTouchRecordForIdentifiers>());
            var dialect = new TFM.Nucleus.Data.PostgreSQL.PostgresSchemaDialect("public", null);

            var physical = SchemaComparer.PhysicalIndexName(table, table["SessionId"], dialect);

            Assert.Equal(IdentifierShortener.Shorten("NCI_tbl_TFM_Nucleus_Tests_TestLongNamedTouchRecordForIdentifiers_SessionId", 63), physical);
            Assert.Contains($"\"{physical}\"", dialect.CreateIndex(table, table["SessionId"]).Text);
        }
    }
}

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// A table whose generated names run past PostgreSQL's 63 bytes: the shape of a visit
    /// table, with a unique id, two indexed lookup columns and text, under a long full name.
    /// </summary>
    [Deletable(DeleteBehavior.Hard)]
    public class TestLongNamedTouchRecordForIdentifiers : CoreObject<TestLongNamedTouchRecordForIdentifiers>
    {
        [DatabaseColumn(Unique = true)]
        public Guid TouchId { get => GetValue<Guid>(); set => SetValue(value); }

        [DatabaseColumn(Indexed = true)]
        public Guid SessionId { get => GetValue<Guid>(); set => SetValue(value); }

        [DatabaseColumn(Indexed = true)]
        public DateTimeOffset Touched { get => GetValue<DateTimeOffset>(); set => SetValue(value); }

        [DatabaseColumn(DataType.Unicode, 64)]
        public string? Source { get => GetValue<string>(); set => SetValue(value); }
    }
}
