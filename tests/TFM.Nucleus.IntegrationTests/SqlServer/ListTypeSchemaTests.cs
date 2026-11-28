using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.IntegrationTests.SqlServer
{
    /// <summary>
    /// The table types a long list is sent as are part of the schema. A database without one is
    /// not current: a provider that may change the schema creates it, and one that may not
    /// refuses to start, with a log that names the type and the statement that creates it, where
    /// it would otherwise start and fail the first query with a long list.
    /// </summary>
    [Collection(SqlServerCollection.Name)]
    public class ListTypeSchemaTests(SqlServerFixture fixture)
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private const string Type = "[dbo].[NucleusBigIntList]";
        private const string Create = $"CREATE TYPE {Type} AS TABLE ([Value] bigint NOT NULL PRIMARY KEY)";

        private (ServiceProvider Services, IDataProvider Provider, RecordingLogs Logs) Build(bool readOnlySchema)
        {
            var logs = new RecordingLogs();
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.AddProvider(logs));
            services.AddNucleus(o =>
            {
                o.AppName = "list-type-test";
                o.ReadOnlySchema = readOnlySchema;
            });
            fixture.Database.AddProvider(services, _ => { });
            var built = services.BuildServiceProvider();
            return (built, built.GetRequiredService<IDataProvider>(), logs);
        }

        [Fact]
        public async Task ADatabaseWithoutAListType_IsNotCurrent_AndOnlyAProviderThatMayChangeTheSchemaCreatesIt()
        {
            fixture.RequireSettings();
            using var schema = await fixture.SchemaLockAsync();
            await fixture.Database.ExecuteAsync($"DROP TYPE {Type}", Ct);
            try
            {
                // Read-only: it does not start, and says what is missing and how to make it.
                var (services, provider, logs) = Build(readOnlySchema: true);
                using (services)
                {
                    Assert.NotEqual(DataProviderStatus.Ok, await provider.InitializeAsync());
                    var refused = await Assert.ThrowsAnyAsync<DataProviderException>(async () =>
                        await provider.CreateContextAsync(ContextOptions.Default, Ct));
                    Assert.Equal(DataProviderErrorKind.SchemaNotCurrent, refused.Kind);
                    Assert.Contains(logs.Entries, e => e.Contains(Type) && e.Contains(Create));
                    Assert.Equal(0, Convert.ToInt32(await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM sys.table_types WHERE name = 'NucleusBigIntList'", Ct)));
                }

                // Allowed to change the schema: it creates the type and starts.
                var (updating, updater, _) = Build(readOnlySchema: false);
                using (updating)
                    Assert.Equal(DataProviderStatus.Ok, await updater.InitializeAsync());
                Assert.Equal(1, Convert.ToInt32(await fixture.Database.ScalarAsync("SELECT COUNT(*) FROM sys.table_types WHERE name = 'NucleusBigIntList'", Ct)));

                // And with the type there, a read-only provider starts.
                var (reading, reader, _) = Build(readOnlySchema: true);
                using (reading)
                    Assert.Equal(DataProviderStatus.Ok, await reader.InitializeAsync());
            }
            finally
            {
                await fixture.Database.ExecuteAsync($"IF TYPE_ID('{Type}') IS NULL {Create}", CancellationToken.None);
            }
        }

        private sealed class RecordingLogs : ILoggerProvider
        {
            public List<string> Entries { get; } = [];

            public ILogger CreateLogger(string categoryName) => new Recorder(Entries);

            public void Dispose() { }

            private sealed class Recorder(List<string> entries) : ILogger
            {
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                {
                    lock (entries)
                        entries.Add(formatter(state, exception));
                }
            }
        }
    }
}
