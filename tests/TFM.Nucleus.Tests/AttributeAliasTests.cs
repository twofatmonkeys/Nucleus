namespace TFM.Nucleus.Tests
{
#pragma warning disable CS0618 // The point of this fixture: entities written with the obsolete names.
    /// <summary>The same columns as <see cref="TestAliasNeutralWidget"/>, declared with the obsolete names.</summary>
    public class TestAliasObsoleteWidget : CoreObject<TestAliasObsoleteWidget>
    {
        [DatabaseColumn(NonClusteredIndex = true, IndexSortDESC = true)]
        public string? Code { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(FullTextIndex = true)]
        public string? Text { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(Filestream = true)]
        public byte[]? Blob { get => GetValue<byte[]>(); set => SetValue(value); }

        [DatabaseColumn(SetValueOverride = eServerFunction.SYSDATETIME)]
        public DateTime Stamp { get => GetValue<DateTime>(); set => SetValue(value); }
    }
#pragma warning restore CS0618

    public class TestAliasNeutralWidget : CoreObject<TestAliasNeutralWidget>
    {
        [DatabaseColumn(Indexed = true, IndexDescending = true)]
        public string? Code { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(FullTextSearch = true)]
        public string? Text { get => GetValue<string>(); set => SetValue(value); }

        [DatabaseColumn(ExternalStorage = true)]
        public byte[]? Blob { get => GetValue<byte[]>(); set => SetValue(value); }

        [DatabaseColumn(SetValueOverride = eServerFunction.CurrentDateTime)]
        public DateTime Stamp { get => GetValue<DateTime>(); set => SetValue(value); }
    }

    /// <summary>
    /// The neutral attribute names replace the SQL Server ones without breaking entities that
    /// still use the old ones: either spelling sets the same flag, and both build the same table.
    /// </summary>
    public class AttributeAliasTests
    {
        [Fact]
        public void ObsoleteAndNeutralNames_BuildTheSameColumns()
        {
            var obsolete = new TableSchema(ObjectSchema.Get<TestAliasObsoleteWidget>());
            var neutral = new TableSchema(ObjectSchema.Get<TestAliasNeutralWidget>());

            foreach (var name in new[] { "Code", "Text", "Blob", "Stamp" })
            {
                var a = obsolete[name];
                var b = neutral[name];
                Assert.Equal(b.Indexed, a.Indexed);
                Assert.Equal(b.IndexSortDescending, a.IndexSortDescending);
                Assert.Equal(b.FullTextSearch, a.FullTextSearch);
                Assert.Equal(b.ExternalStorage, a.ExternalStorage);
            }
            Assert.True(neutral["Code"].Indexed);
            Assert.True(neutral["Code"].IndexSortDescending);
            Assert.True(neutral["Text"].FullTextSearch);
            Assert.True(neutral["Blob"].ExternalStorage);
        }

        [Fact]
        public void ObsoleteAndNeutralNames_ProduceTheSameDdl()
        {
            static string Ddl<T>() where T : ICoreObject =>
                string.Join("\n", SqlServerSchemaDialect.CreateTableStatements(new TableSchema(ObjectSchema.Get<T>())))
                    .Replace(nameof(TestAliasObsoleteWidget), "W").Replace(nameof(TestAliasNeutralWidget), "W");

            Assert.Equal(Ddl<TestAliasNeutralWidget>(), Ddl<TestAliasObsoleteWidget>());
        }

        [Fact]
        public void InterfaceMembers_ReadEitherSpelling()
        {
#pragma warning disable CS0618
            IDatabaseColumnAttribute old = new DatabaseColumnAttribute("X") { NonClusteredIndex = true, FullTextIndex = true, IndexSortDESC = true, Filestream = true };
            IDatabaseColumnAttribute neutral = new DatabaseColumnAttribute("X") { Indexed = true, FullTextSearch = true, IndexDescending = true, ExternalStorage = true };

            foreach (var attribute in new[] { old, neutral })
            {
                Assert.True(attribute.Indexed && attribute.NonClusteredIndex);
                Assert.True(attribute.FullTextSearch && attribute.FullTextIndex);
                Assert.True(attribute.IndexDescending && attribute.IndexSortDESC);
                Assert.True(attribute.ExternalStorage && attribute.Filestream);
            }
#pragma warning restore CS0618
        }

        [Fact]
        public void ServerFunctionAliases_ShareTheirValues()
        {
#pragma warning disable CS0618
            Assert.Equal((int)eServerFunction.SYSDATETIME, (int)eServerFunction.CurrentDateTime);
            Assert.Equal((int)eServerFunction.SYSDATETIMEOFFSET, (int)eServerFunction.CurrentDateTimeOffset);
#pragma warning restore CS0618
            Assert.Same(ServerFunction.SysDateTimeOffset, ServerFunction.Get(eServerFunction.CurrentDateTimeOffset));
        }
    }
}
