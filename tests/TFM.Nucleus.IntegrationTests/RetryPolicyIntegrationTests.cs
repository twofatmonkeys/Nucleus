using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Testing;

namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// The retry policy against a real database: a manager save that cannot get its lock is
    /// attempted again and succeeds once the lock is released; one that never gets it is reported
    /// after its attempts, no longer transient; and a save through a caller's context is never
    /// attempted again. The lock is held outside Nucleus on the failure suite's probe table.
    /// </summary>
    public abstract class RetryPolicyIntegrationTests(DatabaseFixture fixture)
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static string ProbeTable => NucleusEntityTable.For(typeof(IntFailureProbe)).TableName;

        /// <summary>Makes a statement that waits for a lock give up after about a second.</summary>
        protected abstract void GiveUpWaitingSoon(RelationalProviderOptions options);

        private async Task<NucleusTestStack> StackAsync(int retryAttempts)
        {
            using (await fixture.SchemaLockAsync())
                return await fixture.StartStackAsync(options =>
                {
                    options.RetryAttempts = retryAttempts;
                    GiveUpWaitingSoon(options);
                });
        }

        [Fact]
        public async Task ASaveThatCannotGetItsLock_IsAttemptedAgain_AndSucceedsOnceItIsReleased()
        {
            fixture.RequireSettings();
            using var suite = await fixture.LockAsync("failures");
            using var stack = await StackAsync(retryAttempts: 6);
            var probe = new IntFailureProbe { Name = $"retried-{Guid.NewGuid():N}" };

            var held = await fixture.Database.HoldWriteLockAsync(ProbeTable, Ct);
            // Released after the first attempt has given up, while the policy waits to try again.
            var release = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(1.5), Ct);
                await held.DisposeAsync();
            }, Ct);
            try
            {
                await stack.Manager.SaveAsync<IntFailureProbe>([probe], Ct);
            }
            finally
            {
                await release;
            }

            Assert.False(probe.IsNew);
            Assert.NotNull(await stack.Manager.LoadByIDAsync<IntFailureProbe>(probe.ID, Ct));
            Assert.Contains(stack.Log.Entries, e => e.Contains("failed transiently", StringComparison.Ordinal));
        }

        [Fact]
        public async Task ASaveThatNeverGetsItsLock_IsReportedAfterItsAttempts_NoLongerTransient()
        {
            fixture.RequireSettings();
            using var suite = await fixture.LockAsync("failures");
            using var stack = await StackAsync(retryAttempts: 2);
            var probe = new IntFailureProbe { Name = $"exhausted-{Guid.NewGuid():N}" };

            DatabaseException failure;
            await using (await fixture.Database.HoldWriteLockAsync(ProbeTable, Ct))
                failure = await Assert.ThrowsAsync<DatabaseException>(() => stack.Manager.SaveAsync<IntFailureProbe>([probe], Ct));

            Assert.Equal(DatabaseErrorKind.Timeout, failure.Kind);
            Assert.Equal(2, failure.Attempts);
            Assert.False(failure.IsTransient);
            Assert.False(failure.MayHaveBeenApplied);
            Assert.True(probe.IsNew);
        }

        [Fact]
        public async Task ASaveThroughACallersContext_IsNotAttemptedAgain()
        {
            fixture.RequireSettings();
            using var suite = await fixture.LockAsync("failures");
            using var stack = await StackAsync(retryAttempts: 3);
            var probe = new IntFailureProbe { Name = $"caller-{Guid.NewGuid():N}" };

            DatabaseException failure;
            await using (await fixture.Database.HoldWriteLockAsync(ProbeTable, Ct))
            {
                failure = await Assert.ThrowsAsync<DatabaseException>(async () =>
                {
                    await using var context = await stack.Manager.CreateContextAsync<IntFailureProbe>(ContextOptions.Transactional, Ct);
                    await context.SaveAsync(probe, Ct);
                    await context.CommitAsync(Ct);
                });
            }

            Assert.Equal(1, failure.Attempts);
            Assert.True(failure.IsTransient, "Left to the caller, which owns the transaction.");
        }
    }
}
