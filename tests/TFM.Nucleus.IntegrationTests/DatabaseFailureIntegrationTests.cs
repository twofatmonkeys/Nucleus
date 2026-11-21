using System.Data.Common;
using System.Linq.Expressions;
using TFM.Nucleus.Data.Providers;
using TFM.Nucleus.Testing;

namespace TFM.Nucleus.IntegrationTests
{
    /// <summary>
    /// What a caller is told when the database fails an operation, on every engine: a
    /// <see cref="DatabaseException"/> one level over the driver's exception, saying what kind of
    /// failure it was, whether another attempt is worth making, and whether the operation may have
    /// been applied. A statement that cannot get its lock is the failure these tests provoke, since
    /// a lock held outside Nucleus makes it happen on demand; the probe's table is locked so no other
    /// suite waits.
    /// </summary>
    public abstract class DatabaseFailureIntegrationTests(DatabaseFixture fixture)
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static string ProbeTable => NucleusEntityTable.For(typeof(IntFailureProbe)).TableName;

        /// <summary>Whether a read waits for a writer's lock. SQLite in write-ahead log mode reads past it.</summary>
        protected virtual bool ReadsWaitForWriters => true;

        /// <summary>
        /// Where a transactional write waits for a writer's lock: at its first statement, or, on
        /// SQLite, whose transactions take the write lock as they begin, when its context opens.
        /// </summary>
        protected virtual DatabaseOperation BlockedWriteFailsAt => DatabaseOperation.Write;

        /// <summary>Points the options at a server, or a file, there is nothing at.</summary>
        protected abstract void PointAtNothing(RelationalProviderOptions options);

        private static ContextOptions TimingOut(bool transactional) => new() { UseTransaction = transactional, StatementTimeoutSeconds = 1 };

        [Fact]
        public async Task ABlockedSave_IsATransientTimeout_ThatAppliedNothing()
        {
            fixture.RequireSettings();
            using var suite = await fixture.LockAsync("failures");
            var probe = new IntFailureProbe { Name = $"blocked-{Guid.NewGuid():N}" };

            DatabaseException failure;
            await using (await fixture.Database.HoldWriteLockAsync(ProbeTable, Ct))
            {
                failure = await Assert.ThrowsAsync<DatabaseException>(async () =>
                {
                    await using var context = await fixture.Provider!.CreateContextAsync(TimingOut(transactional: true), Ct);
                    await context.SaveAsync(probe, Ct);
                    await context.CommitAsync(Ct);
                });
            }

            Assert.Equal(DatabaseErrorKind.Timeout, failure.Kind);
            Assert.Equal(BlockedWriteFailsAt, failure.Operation);
            Assert.False(failure.MayHaveBeenApplied);
            Assert.True(failure.IsTransient);
            Assert.IsAssignableFrom<DbException>(failure.InnerException);
            Assert.Null(await fixture.Manager!.LoadByIDAsync<IntFailureProbe>(probe.ID, cancellationToken: Ct));
        }

        /// <summary>A provider's own StatementTimeoutSeconds limits a manager save, with no context option.</summary>
        [Fact]
        public async Task ASaveBlockedPastTheProvidersStatementTimeout_IsATransientTimeout()
        {
            fixture.RequireSettings();
            using var suite = await fixture.LockAsync("failures");
            NucleusTestStack stack;
            using (await fixture.SchemaLockAsync())
                stack = await fixture.StartStackAsync(options => options.StatementTimeoutSeconds = 1);
            using var _ = stack;
            var probe = new IntFailureProbe { Name = $"blocked-manager-{Guid.NewGuid():N}" };

            DatabaseException failure;
            await using (await fixture.Database.HoldWriteLockAsync(ProbeTable, Ct))
                failure = await Assert.ThrowsAsync<DatabaseException>(() => stack.Manager.SaveAsync<IntFailureProbe>([probe], Ct));

            Assert.Equal(DatabaseErrorKind.Timeout, failure.Kind);
            Assert.True(failure.IsTransient);
            Assert.True(probe.IsNew, "A save that failed must leave the object unsaved, for a retry.");
        }

        [Fact]
        public async Task ABlockedRead_IsATransientTimeout_OfARead()
        {
            fixture.RequireSettings();
            Assert.SkipUnless(ReadsWaitForWriters, $"{fixture.ProviderName} reads past a writer's lock.");
            using var suite = await fixture.LockAsync("failures");
            var id = Guid.NewGuid();
            Expression<Func<IntFailureProbe, bool>> predicate = p => p.ID == id;

            DatabaseException failure;
            await using (await fixture.Database.HoldWriteLockAsync(ProbeTable, Ct))
            {
                await using var context = await fixture.Provider!.CreateContextAsync(TimingOut(transactional: false), Ct);
                failure = await Assert.ThrowsAsync<DatabaseException>(() => context.LoadAsync<IntFailureProbe>(predicate));
            }

            Assert.Equal(DatabaseErrorKind.Timeout, failure.Kind);
            Assert.Equal(DatabaseOperation.Read, failure.Operation);
            Assert.False(failure.MayHaveBeenApplied);
            Assert.True(failure.IsTransient);
        }

        /// <summary>The caller's own cancellation is never reported as a database failure, though the engine may report it as its error.</summary>
        [Fact]
        public async Task ACancelledRead_IsTheCancellation()
        {
            fixture.RequireSettings();
            Assert.SkipUnless(ReadsWaitForWriters, $"{fixture.ProviderName} reads past a writer's lock.");
            using var suite = await fixture.LockAsync("failures");
            var id = Guid.NewGuid();

            await using (await fixture.Database.HoldWriteLockAsync(ProbeTable, Ct))
            {
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
                cancel.CancelAfter(TimeSpan.FromMilliseconds(500));
                var thrown = await Record.ExceptionAsync(() => fixture.Manager!.LoadAsync<IntFailureProbe>(p => p.ID == id, cancel.Token));
                Assert.IsAssignableFrom<OperationCanceledException>(thrown);
            }
        }

        [Fact]
        public async Task AUniqueViolation_IsADatabaseException_OneLevelOverTheDriver()
        {
            var manager = fixture.RequireManager();
            var code = $"uq-failure-{Guid.NewGuid():N}";
            await manager.SaveAsync(new IntUniqueItem { Code = code });

            var violation = await Assert.ThrowsAsync<UniqueViolationException>(() => manager.SaveAsync(new IntUniqueItem { Code = code }));

            DatabaseException failure = violation;
            Assert.Equal(DatabaseErrorKind.UniqueViolation, failure.Kind);
            Assert.Equal(DatabaseOperation.Write, failure.Operation);
            Assert.False(failure.MayHaveBeenApplied);
            Assert.False(failure.IsTransient);
            Assert.IsAssignableFrom<DbException>(violation.InnerException);
        }

        [Fact]
        public async Task AnUnreachableDatabase_FailsToOpen_AsAConnectionFailure()
        {
            fixture.RequireSettings();
            NucleusTestStack stack;
            using (await fixture.SchemaLockAsync())
                stack = await fixture.StartStackAsync(_ => { });
            using var _ = stack;
            var options = ((RelationalDataProvider)stack.Provider).Options;
            PointAtNothing(options);

            var failure = await Assert.ThrowsAsync<DatabaseException>(async () =>
                await stack.Provider.CreateContextAsync(ContextOptions.Transactional, Ct));
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{fixture.ProviderName}: {failure.Kind}, transient {failure.IsTransient}, {failure.Attempts} attempt(s). {failure.Message}");

            Assert.Equal(DatabaseOperation.Open, failure.Operation);
            Assert.Contains(failure.Kind, new[] { DatabaseErrorKind.ConnectionFailed, DatabaseErrorKind.Timeout });
            Assert.False(failure.MayHaveBeenApplied);
            Assert.InRange(failure.Attempts, 1, options.ConnectAttempts);
            Assert.True(failure.Attempts == 1 || failure.IsTransient, "Only a transient failure is attempted again.");
            // A context that never opened gives its connection slot back.
            var slots = ((RelationalDataProvider)stack.Provider).ConnectionSlots;
            Assert.Equal(slots.Capacity, slots.Available);
        }
    }
}
