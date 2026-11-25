using System.Transactions;
using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// MaxConnectionPoolSize is the one limit on a provider's connections, and no connection
    /// enlists in an ambient transaction. A transactional context opened inside a TransactionScope
    /// would therefore commit outside it, so it is refused, on every engine; a read is not
    /// transactional and still runs.
    /// </summary>
    public abstract class ConnectionLimitIntegrationTests(DatabaseFixture fixture)
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        /// <summary>A full budget is a failure of the database to the caller, known not to be worth another attempt.</summary>
        private static void AssertPoolExhausted(Exception ex)
        {
            var found = Assert.IsType<DatabaseException>(ex);
            Assert.Equal(DatabaseErrorKind.PoolExhausted, found.Kind);
            Assert.Equal(DatabaseOperation.Open, found.Operation);
            Assert.False(found.IsTransient);
        }

        private static void AssertKind(Exception ex, DataContextErrorKind kind)
        {
            var found = ExceptionChain.Find<DataContextException>(ex);
            Assert.True(found != null, $"Expected a DataContextException of kind {kind}, got: {ex}");
            Assert.Equal(kind, found!.Kind);
        }

        [Fact]
        public async Task ATransactionalContext_InsideAnAmbientTransaction_IsRefused()
        {
            fixture.RequireSettings();
            using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);

            var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
                await fixture.Provider!.CreateContextAsync(ContextOptions.Transactional, Ct));
            AssertKind(ex, DataContextErrorKind.AmbientTransaction);

            AssertKind(Assert.ThrowsAny<Exception>(() => fixture.Provider!.CreateContext(ContextOptions.Transactional)),
                DataContextErrorKind.AmbientTransaction);
        }

        [Fact]
        public async Task ASave_InsideAnAmbientTransaction_IsRefused_AndStoresNothing()
        {
            fixture.RequireSettings();
            var widget = new IntWidget { Name = $"ambient-{Guid.NewGuid():N}", Quantity = 1 };

            using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
            {
                var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await fixture.Manager!.SaveAsync(widget));
                AssertKind(ex, DataContextErrorKind.AmbientTransaction);
            }

            Assert.Null(await fixture.Manager!.LoadByIDAsync<IntWidget>(widget.ID, cancellationToken: Ct));
        }

        [Fact]
        public async Task ARead_InsideAnAmbientTransaction_StillRuns()
        {
            fixture.RequireSettings();
            var widget = new IntWidget { Name = $"ambient-read-{Guid.NewGuid():N}", Quantity = 1 };
            await fixture.Manager!.SaveAsync(widget);

            using var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
            var loaded = await fixture.Manager!.LoadByIDAsync<IntWidget>(widget.ID, cancellationToken: Ct);

            Assert.NotNull(loaded);
        }

        /// <summary>A full budget fails as PoolExhausted after the pool wait, and a context disposed gives its slot back.</summary>
        [Fact]
        public async Task ContextsPastTheLimit_FailAsPoolExhausted_UntilOneIsDisposed()
        {
            fixture.RequireSettings();
            using var schema = await fixture.SchemaLockAsync();
            using var stack = await fixture.StartStackAsync(options =>
            {
                options.MaxConnectionPoolSize = 2;
                options.PoolWaitTimeoutSeconds = 1;
            });

            var first = await stack.Provider.CreateContextAsync(ContextOptions.Transactional, Ct);
            await using var second = await stack.Provider.CreateContextAsync(ContextOptions.Default, Ct);

            var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await stack.Provider.CreateContextAsync(ContextOptions.Default, Ct));
            AssertPoolExhausted(ex);
            AssertPoolExhausted(Assert.ThrowsAny<Exception>(() => stack.Provider.CreateContext(ContextOptions.Transactional)));

            await first.CommitAsync(Ct);
            await first.DisposeAsync();
            await using var third = await stack.Provider.CreateContextAsync(ContextOptions.Transactional, Ct);
            await third.CommitAsync(Ct);
        }
    }
}
