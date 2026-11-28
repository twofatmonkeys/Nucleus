using Microsoft.Extensions.DependencyInjection;
using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Testing;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// The PostgreSQL test stack caps its connection pool. At the provider's default of 100, one
    /// test process could take every connection PostgreSQL allows by default, and the net8.0 and
    /// net10.0 runs share the server, so a store's suite failed with 53300 "too many clients"
    /// whenever their PostgreSQL tests overlapped. Nothing here opens a connection.
    /// </summary>
    public class PostgresTestPoolTests
    {
        private static readonly PostgresTestDatabase Database =
            new(new NucleusTestDatabaseSettings("localhost:5432", "nucleus_test", "nucleus", "unused"));

        private static RelationalProviderOptions OptionsAfter(Action<IServiceCollection> register)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNucleus(o => o.AppName = "pool-test");
            register(services);
            using var built = services.BuildServiceProvider();
            return Assert.IsAssignableFrom<RelationalDataProvider>(built.GetRequiredService<IDataProvider>()).Options;
        }

        [Fact]
        public void AStack_CapsItsPool()
        {
            var options = OptionsAfter(services => Database.AddProvider(services, _ => { }));

            Assert.Equal(20, options.MaxConnectionPoolSize);
            // Each runtime has a database of its own, so the two runs of a suite never share one.
            Assert.Equal($"nucleus_test_net{Environment.Version.Major}", options.Database);
            Assert.Equal(options.Database, Database.DatabaseName);
        }

        [Fact]
        public void ATestsOwnOptions_CanChangeTheCap()
        {
            var options = OptionsAfter(services => Database.AddProvider(services, o => o.MaxConnectionPoolSize = 8));

            Assert.Equal(8, options.MaxConnectionPoolSize);
        }

        [Fact]
        public void ARootedStack_CapsItsPoolToo()
        {
            var options = OptionsAfter(services => Database.AddRootedProvider(services, typeof(TestWidget), _ => { }));

            Assert.Equal(20, options.MaxConnectionPoolSize);
        }
    }
}
