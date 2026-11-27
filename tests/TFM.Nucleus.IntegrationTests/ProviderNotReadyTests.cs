using Microsoft.Extensions.DependencyInjection;
using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// A provider that could not initialise says why. Before, initialisation logged the failure
    /// and kept only a status, so the caller was refused with a provider exception of kind
    /// NotInitialized that had no message and no inner exception, whatever had gone wrong: a
    /// database that could not be reached looked the same as a schema that was not current. Now a
    /// database that failed is a DatabaseException like any other failure of it, and a deployment
    /// that is wrong keeps its own kind and message.
    /// </summary>
    /// <remarks>Needs no test database: it points each provider at nothing.</remarks>
    public class ProviderNotReadyTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static IDataProvider Build(Action<IServiceCollection> addProvider)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNucleus(o => o.AppName = "not-ready-test");
            addProvider(services);
            return services.BuildServiceProvider().GetRequiredService<IDataProvider>();
        }

        public static TheoryData<string> ServerEngines => new() { "SqlServer", "Postgres" };

        /// <summary>A server address nothing listens on, so the first use fails at the catalog read.</summary>
        private static IDataProvider Unreachable(string engine) => engine == "SqlServer"
            ? Build(s => s.AddNucleusSqlServerDataProvider(o =>
            {
                o.Server = "127.0.0.1:1";
                o.Database = "nowhere";
                o.Username = "nobody";
                o.Password = "nothing";
                o.ConnectTimeoutSeconds = 1;
                o.ConnectAttempts = 1;
            }))
            : Build(s => s.AddNucleusPostgresDataProvider(o =>
            {
                o.Server = "127.0.0.1:1";
                o.Database = "nowhere";
                o.Username = "nobody";
                o.Password = "nothing";
                o.ConnectTimeoutSeconds = 1;
                o.ConnectAttempts = 1;
            }));

        [Theory]
        [MemberData(nameof(ServerEngines))]
        public async Task ADatabaseThatCannotBeReachedOnFirstUse_IsADatabaseException_OneLevelOverTheDriver(string engine)
        {
            var provider = Unreachable(engine);

            var failure = await Assert.ThrowsAsync<DatabaseException>(async () =>
                await provider.CreateContextAsync(ContextOptions.Default, Ct));
            var sync = Assert.Throws<DatabaseException>(() => provider.CreateContext(ContextOptions.Default));

            TestContext.Current.TestOutputHelper?.WriteLine($"{engine} async: {failure.Kind}, {failure.Operation}. {failure.InnerException}");
            TestContext.Current.TestOutputHelper?.WriteLine($"{engine} sync: {sync.Kind}, {sync.Operation}. {sync.InnerException}");
            Assert.Contains(failure.Kind, new[] { DatabaseErrorKind.ConnectionFailed, DatabaseErrorKind.Timeout });
            Assert.False(failure.MayHaveBeenApplied);
            Assert.Contains("could not be initialised", failure.Message);
            Assert.NotNull(failure.InnerException);
            Assert.IsNotAssignableFrom<NucleusException>(failure.InnerException);
            // Each caller gets an exception of its own, so one thrown on two threads is never shared.
            Assert.NotSame(failure, sync);
            // The synchronous open has no budget of its own, so the driver reports the failure itself.
            Assert.Contains(sync.Kind, new[] { DatabaseErrorKind.ConnectionFailed, DatabaseErrorKind.Timeout });
            Assert.IsNotAssignableFrom<NucleusException>(sync.InnerException);
        }

        [Fact]
        public async Task ADeploymentThatIsWrong_KeepsItsKindAndItsMessage()
        {
            var provider = Build(s => s.AddNucleusSqliteDataProvider(o => o.Database = Path.Combine(Path.GetTempPath(), "nucleus\0not-a-path.db")));

            var failure = await Assert.ThrowsAnyAsync<DataProviderException>(async () =>
                await provider.CreateContextAsync(ContextOptions.Default, Ct));

            Assert.Equal(DataProviderErrorKind.NotConfigured, failure.Kind);
            Assert.Contains("not a usable file path", failure.Message);
        }
    }
}
