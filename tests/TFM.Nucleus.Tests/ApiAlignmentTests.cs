using TFM.Nucleus.Data.PostgreSQL;
using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// The names aligned across the surface: each old name still reads and writes the value its
    /// replacement holds, so code compiled against either sees the same setting.
    /// </summary>
    public class ApiAlignmentTests
    {
#pragma warning disable CS0618 // the obsolete names are the subject of these tests
        [Fact]
        public void TheSharedOptions_KeepTheirOldNames()
        {
            var options = new SQLConfiguration();

            options.MaxParametersPerQuery = 123;
            Assert.Equal(123, options.MaxParametersPerCommand);
            options.MaxParametersPerCommand = 456;
            Assert.Equal(456, options.MaxParametersPerQuery);

            options.BulkCopyTimeoutSeconds = 7;
            Assert.Equal(7, options.BulkTimeoutSeconds);
            Assert.Throws<ArgumentOutOfRangeException>(() => options.BulkTimeoutSeconds = -1);
        }

        [Fact]
        public void TheSqlServerOptions_KeepTheirOldNames()
        {
            var options = new SQLConfiguration();

            options.SQLPacketSize = 4096;
            Assert.Equal(4096, options.PacketSize);
            options.BulkCopyBatchSize = 10;
            Assert.Equal(10, options.BulkBatchSize);
            Assert.Throws<ArgumentOutOfRangeException>(() => options.BulkBatchSize = 0);
        }

        [Fact]
        public void ThePostgresOptions_CountAttempts_AndSetTheConnectTimeout()
        {
            var options = new PostgresConfiguration { Server = "db", Database = "nucleus", Username = "user", Password = "secret", AppName = "tests" };

            Assert.Equal(3, options.ConnectAttempts);
            options.ConnectRetryCount = 5;
            Assert.Equal(5, options.ConnectAttempts);

            Assert.Equal(15, options.ConnectTimeoutSeconds);
            options.ConnectTimeoutSeconds = 40;
            Assert.Contains("Timeout=40", options.BuildConnectionString("nucleus", useTransaction: false));
            Assert.Throws<ArgumentOutOfRangeException>(() => options.ConnectTimeoutSeconds = -1);
        }

        [Fact]
        public void AQuery_AllowsADirtyRead_UnderEitherName()
        {
            var query = new SelectQuery(SqlServerQueryRenderer.Instance, new TableSchema(ObjectSchema.Get<TestWidget>()));

            query.AllowDirtyRead = true;
            Assert.True(query.WithNoLock);
            query.WithNoLock = false;
            Assert.False(query.AllowDirtyRead);
        }

        [Fact]
        public void ContextOptions_WatchForChanges_UnderEitherName()
        {
            Assert.True(new ContextOptions { UseDependency = true }.WatchForChanges);
            Assert.True(new ContextOptions { WatchForChanges = true }.UseDependency);
        }

        [Fact]
        public void TheSqlServerException_ReportsItsKind_UnderEitherName()
        {
            var ex = Assert.Throws<SqlDataProviderException>(() => new SQLConfiguration { Server = "db" }.BuildNewSQLConnection(useTransaction: false));

            Assert.Equal(DataProviderErrorKind.NotConfigured, ex.Kind);
            Assert.Equal(SqlDataProviderExceptionType.NotConfigured, ex.Type);
        }
#pragma warning restore CS0618

        [Theory]
        [InlineData(typeof(UniqueViolationException))]
        [InlineData(typeof(CoreObjectException))]
        [InlineData(typeof(ObjectSchemaException))]
        [InlineData(typeof(InvalidObjectTypeException))]
        [InlineData(typeof(PropertyNotFoundException))]
        [InlineData(typeof(PropertySchemaException))]
        [InlineData(typeof(DataProviderException))]
        [InlineData(typeof(DataContextException))]
        [InlineData(typeof(InvalidQueryException))]
        [InlineData(typeof(SqlDataProviderException))]
        [InlineData(typeof(PostgresDataProviderException))]
        [InlineData(typeof(TFM.Nucleus.Data.SQLite.SqliteDataProviderException))]
        public void EveryNucleusException_SharesOneBase(Type exception) =>
            Assert.True(typeof(NucleusException).IsAssignableFrom(exception), $"{exception.Name} does not derive from NucleusException.");

        [Fact]
        public void ARowPage_SaysWhetherRowsRemain()
        {
            Assert.True(new RowPage([], 10, Skip: 0, Take: 5).HasMore);
            Assert.False(new RowPage([], 0, Skip: 0, Take: 5).HasMore);
        }
    }
}
