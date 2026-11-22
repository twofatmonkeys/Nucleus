using Npgsql;
using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.IntegrationTests.Postgres
{
    /// <summary>
    /// The server sees no more than MaxConnectionPoolSize connections from a provider. Its
    /// transactional and other connections had connection strings that differed by Enlist, and
    /// Npgsql pools by connection string, so a provider held two pools of that size; a store's
    /// suite ran out of server connections that way. A change subscription's LISTEN connection is
    /// one of the limit too.
    /// </summary>
    [Collection(PostgresCollection.Name)]
    public sealed class PostgresConnectionLimitTests(PostgresFixture fixture)
    {
        private const int Limit = 3;

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        /// <summary>
        /// A stack whose connections carry an application name of their own, so the server's
        /// count of them is this test's alone. It goes through a raw connection string, which also
        /// shows the limit overwrites a raw string's pool size.
        /// </summary>
        private Task<NucleusTestStack> StartCountedStackAsync(string appName) => fixture.StartStackAsync(options =>
        {
            var settings = fixture.RequireSettings();
            var parts = settings.Server.Split(':');
            options.ConnectionString = new NpgsqlConnectionStringBuilder
            {
                Host = parts[0],
                Port = parts.Length > 1 ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 5432,
                Username = settings.Username,
                Password = settings.Password,
                ApplicationName = appName,
                MaxPoolSize = 100,
            }.ConnectionString;
            options.MaxConnectionPoolSize = Limit;
            options.PoolWaitTimeoutSeconds = 1;
        });

        private async Task<long> ServerConnectionsAsync(string appName) =>
            Convert.ToInt64(await fixture.Database.ScalarAsync(
                $"SELECT count(*) FROM pg_stat_activity WHERE application_name = '{appName}'", Ct), System.Globalization.CultureInfo.InvariantCulture);

        private static async Task<List<IDataContext>> OpenAsync(IDataProvider provider, ContextOptions options, int count)
        {
            var contexts = new List<IDataContext>();
            for (var i = 0; i < count; i++)
                contexts.Add(await provider.CreateContextAsync(options, Ct));
            return contexts;
        }

        private static async Task CloseAsync(List<IDataContext> contexts)
        {
            foreach (var context in contexts)
            {
                if (context.HasTransaction)
                    await context.CommitAsync(Ct);
                await context.DisposeAsync();
            }
        }

        [Fact]
        public async Task TransactionalAndOtherContexts_ShareOnePool()
        {
            fixture.RequireSettings();
            var appName = $"pool-limit-{Guid.NewGuid():N}";
            using var schema = await fixture.SchemaLockAsync();
            using var stack = await StartCountedStackAsync(appName);

            await CloseAsync(await OpenAsync(stack.Provider, ContextOptions.Transactional, Limit));
            await CloseAsync(await OpenAsync(stack.Provider, ContextOptions.Default, Limit));

            // Both rounds' connections are idle in the pool now; with two pools there were twice the limit.
            Assert.InRange(await ServerConnectionsAsync(appName), 1, Limit);
        }

        [Fact]
        public async Task ASubscription_HoldsASlot_UntilItIsDisposed()
        {
            fixture.RequireSettings();
            var appName = $"pool-limit-{Guid.NewGuid():N}";
            using var schema = await fixture.SchemaLockAsync();
            using var stack = await StartCountedStackAsync(appName);

            var subscription = await stack.Provider.SubscribeToChangesAsync<IntHardItem>(Ct);
            var contexts = await OpenAsync(stack.Provider, ContextOptions.Transactional, Limit - 1);

            var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await stack.Provider.CreateContextAsync(ContextOptions.Default, Ct));
            Assert.Equal(DataContextErrorKind.PoolExhausted, ExceptionChain.Find<DataContextException>(ex)?.Kind);
            Assert.InRange(await ServerConnectionsAsync(appName), 1, Limit);

            await subscription.DisposeAsync();
            await using (await stack.Provider.CreateContextAsync(ContextOptions.Default, Ct))
            {
            }
            await CloseAsync(contexts);
        }
    }
}
