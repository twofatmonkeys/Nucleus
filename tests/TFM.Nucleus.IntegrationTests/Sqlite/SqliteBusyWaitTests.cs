using System.Diagnostics;
using TFM.Nucleus.Testing;

namespace TFM.Nucleus.IntegrationTests.Sqlite
{
    /// <summary>
    /// SQLite's command timeout is the wait for another connection's write lock, not a limit on a
    /// running statement, so the provider leaves it at BusyTimeoutSeconds: a statement timeout set
    /// as the command timeout would have made every blocked writer wait that long instead.
    /// </summary>
    [Collection(SqliteCollection.Name)]
    public sealed class SqliteBusyWaitTests(SqliteFixture fixture)
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task AStatementTimeout_DoesNotLengthenTheWaitForTheWriteLock()
        {
            fixture.RequireSettings();
            using var suite = await fixture.LockAsync("failures");
            var probe = new IntFailureProbe { Name = $"busy-{Guid.NewGuid():N}" };
            var clock = Stopwatch.StartNew();

            DatabaseException failure;
            await using (await fixture.Database.HoldWriteLockAsync(NucleusEntityTable.For(typeof(IntFailureProbe)).TableName, Ct))
            {
                // Not transactional, so the write waits at its statement rather than as the context opens.
                await using var context = await fixture.Provider!.CreateContextAsync(new ContextOptions { StatementTimeoutSeconds = 120 }, Ct);
                failure = await Assert.ThrowsAsync<DatabaseException>(() => context.SaveAsync(probe, Ct));
            }

            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), $"The blocked write waited {clock.Elapsed.TotalSeconds:0.0} s.");
            Assert.Equal(DatabaseErrorKind.Timeout, failure.Kind);
            Assert.Equal(DatabaseOperation.Write, failure.Operation);
            // A write with no transaction is reported as possibly applied, so it is not called transient.
            Assert.True(failure.MayHaveBeenApplied);
            Assert.False(failure.IsTransient);
        }
    }
}
