namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// The SQL Server connect timeout was a fixed 3 seconds, which a busy server's login phase
    /// outran under load; it is a setting now, 3 seconds by default.
    /// </summary>
    public class SqlServerConfigurationTests
    {
        private static SQLConfiguration Configuration() => new()
        {
            Server = "db",
            Database = "nucleus",
            Username = "user",
            Password = "secret",
            AppName = "tests",
        };

        [Fact]
        public void ConnectTimeout_DefaultsToThreeSeconds_AndReachesEveryConnection()
        {
            var options = Configuration();

            Assert.Equal(3, options.ConnectTimeoutSeconds);
            Assert.Equal(3, options.BuildNewSQLConnection(useTransaction: true).ConnectionTimeout);

            options.ConnectTimeoutSeconds = 20;
            Assert.Equal(20, options.BuildNewSQLConnection(useTransaction: true).ConnectionTimeout);
            Assert.Equal(20, options.BuildNewSQLConnection(useTransaction: false).ConnectionTimeout);
            Assert.Equal(20, options.BuildSystemSQLConnection().ConnectionTimeout);
        }

        [Fact]
        public void ConnectTimeout_AllowsNoLimit_AndRefusesANegative()
        {
            var options = Configuration();

            options.ConnectTimeoutSeconds = 0;
            Assert.Equal(0, options.BuildNewSQLConnection(useTransaction: true).ConnectionTimeout);
            Assert.Throws<ArgumentOutOfRangeException>(() => options.ConnectTimeoutSeconds = -1);
        }
    }
}
