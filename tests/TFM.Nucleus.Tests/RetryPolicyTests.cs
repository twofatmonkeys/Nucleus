namespace TFM.Nucleus.Tests
{
    /// <summary>A widget that counts its save hooks, to show a retried save runs them once.</summary>
    public class RetryCountingWidget : CoreObject<RetryCountingWidget>
    {
        public int PreSaveRuns { get; private set; }

        [DatabaseColumn]
        public string? Name { get => GetValue<string>(); set => SetValue(value); }

        public override void PreSave() => PreSaveRuns++;
    }

    /// <summary>
    /// The opt-in retry policy (<c>RetryAttempts</c>) of the operations the manager runs on a
    /// context of its own: off by default; a transient failure that applied nothing is attempted
    /// again on a new context, with the save hooks run once; any other failure, a stream that has
    /// handed out a row, and the last attempt's failure propagate, the last one no longer transient
    /// so a caller does not retry it again.
    /// </summary>
    public class RetryPolicyTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static DatabaseException Transient(DatabaseOperation operation = DatabaseOperation.Write) =>
            new("deadlock", DatabaseErrorKind.Deadlock, transient: true, new Exception("driver")) { Operation = operation };

        private static (ObjectDataManager Manager, FakeDataProvider Provider) Stack(int retryAttempts, params Exception[] failures)
        {
            var provider = new FakeDataProvider { RetryAttempts = retryAttempts };
            foreach (var failure in failures)
                provider.PendingFailures.Enqueue(failure);
            return (new ObjectDataManager(provider, new ObjectSchemaProvider(), null), provider);
        }

        [Fact]
        public async Task ByDefault_ATransientFailure_IsReportedAfterOneAttempt()
        {
            var (manager, provider) = Stack(1, Transient());
            var widget = new TestWidget { Name = "w" };

            var failure = await Assert.ThrowsAsync<DatabaseException>(() => manager.SaveAsync<TestWidget>([widget], Ct));

            Assert.True(failure.IsTransient);
            Assert.Equal(1, failure.Attempts);
            Assert.Single(provider.Contexts);
            Assert.True(widget.IsNew);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ASaveThatFailsTransiently_IsAttemptedAgainOnANewContext_WithItsHooksRunOnce(bool async)
        {
            var (manager, provider) = Stack(3, Transient());
            var widget = new RetryCountingWidget { Name = "w" };

            if (async)
                await manager.SaveAsync<RetryCountingWidget>([widget], Ct);
            else
                manager.Save<RetryCountingWidget>([widget]);

            Assert.False(widget.IsNew);
            Assert.Equal(1, widget.PreSaveRuns);
            Assert.Equal(2, provider.Contexts.Count);
            Assert.True(provider.Contexts[0].RolledBack);
            Assert.NotNull(provider.Contexts[1].CommittedAsync);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ABatchOfMixedTypes_IsCheckedOnce_ThoughItIsAttemptedAgain(bool async)
        {
            var (manager, provider) = Stack(3, Transient());
            var widget = new RetryCountingWidget { Name = "w" };

            if (async)
                await manager.SaveAsync([widget], Ct);
            else
                manager.Save([(ICoreObject)widget]);

            Assert.False(widget.IsNew);
            Assert.Equal(1, widget.PreSaveRuns);
            Assert.Equal(2, provider.Contexts.Count);
        }

        [Fact]
        public async Task TheLastAttemptsFailure_IsNotTransient_AndSaysHowManyAttemptsWereMade()
        {
            var (manager, provider) = Stack(2, Transient(), Transient());
            var widget = new TestWidget { Name = "w" };

            var failure = await Assert.ThrowsAsync<DatabaseException>(() => manager.SaveAsync<TestWidget>([widget], Ct));

            Assert.False(failure.IsTransient);
            Assert.Equal(2, failure.Attempts);
            Assert.Equal(DatabaseErrorKind.Deadlock, failure.Kind);
            Assert.Equal("driver", failure.InnerException!.Message);
            Assert.Equal(2, provider.Contexts.Count);
            Assert.True(widget.IsNew);
        }

        [Fact]
        public async Task AFailureThatMayHaveBeenApplied_IsNotAttemptedAgain()
        {
            var unknown = new DatabaseException("commit", DatabaseErrorKind.CommitOutcomeUnknown, transient: false, new Exception())
            {
                MayHaveBeenApplied = true,
                Operation = DatabaseOperation.Commit,
            };
            var (manager, provider) = Stack(3, unknown);

            var failure = await Assert.ThrowsAsync<DatabaseException>(() => manager.SaveAsync<TestWidget>([new TestWidget { Name = "w" }], Ct));

            Assert.Same(unknown, failure);
            Assert.Single(provider.Contexts);
        }

        [Fact]
        public async Task AFailureThatIsNotTransient_IsNotAttemptedAgain()
        {
            var violation = new UniqueViolationException("m", "UQ_x", typeof(TestWidget), nameof(TestWidget.Name), "Name", new Exception());
            var (manager, provider) = Stack(3, violation);

            var failure = await Assert.ThrowsAsync<UniqueViolationException>(() => manager.SaveAsync<TestWidget>([new TestWidget { Name = "w" }], Ct));

            Assert.Same(violation, failure);
            Assert.Single(provider.Contexts);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ALoadThatFailsTransiently_IsAttemptedAgain(bool async)
        {
            var (manager, provider) = Stack(3, Transient(DatabaseOperation.Read));
            provider.SetupRows<TestWidget>(new Dictionary<string, object?> { ["ID"] = Guid.NewGuid(), ["Name"] = "stored" });

            var loaded = async
                ? await manager.LoadAsync<TestWidget>(w => w.Name == "stored", Ct)
                : manager.Load<TestWidget>(w => w.Name == "stored");

            Assert.Equal("stored", Assert.Single(loaded).Name);
            Assert.Equal(2, provider.Contexts.Count);
        }

        [Fact]
        public async Task ADeleteByPredicateThatFailsTransiently_IsAttemptedAgain()
        {
            var (manager, provider) = Stack(3, Transient(DatabaseOperation.Delete));
            provider.RowsAffectedByDeleteWhere = 4;

            Assert.Equal(4, await manager.DeleteWhereAsync<TestDeletableWidget>(w => w.Name == "gone", Ct));
            Assert.Equal(2, provider.Contexts.Count);
        }

        [Fact]
        public async Task AStreamThatFailsBeforeItsFirstRow_IsAttemptedAgain()
        {
            var (manager, provider) = Stack(3, Transient(DatabaseOperation.Read));
            provider.SetupRows<TestWidget>(
                new Dictionary<string, object?> { ["ID"] = Guid.NewGuid(), ["Name"] = "a" },
                new Dictionary<string, object?> { ["ID"] = Guid.NewGuid(), ["Name"] = "b" });

            var names = new List<string?>();
            await foreach (var widget in manager.LoadStreamAsync<TestWidget>(cancellationToken: Ct))
                names.Add(widget.Name);

            Assert.Equal(["a", "b"], names);
            Assert.Equal(2, provider.Contexts.Count);
            Assert.All(provider.Contexts, c => Assert.NotNull(c.DisposedAsync));
        }

        [Fact]
        public async Task AStreamThatFailsAfterARow_IsNotAttemptedAgain()
        {
            var (manager, provider) = Stack(3);
            provider.SetupRows<TestWidget>(
                new Dictionary<string, object?> { ["ID"] = Guid.NewGuid(), ["Name"] = "a" },
                new Dictionary<string, object?> { ["ID"] = Guid.NewGuid(), ["Name"] = "b" });
            var midway = Transient(DatabaseOperation.Read);
            provider.StreamFailure = (1, midway);

            var names = new List<string?>();
            var failure = await Assert.ThrowsAsync<DatabaseException>(async () =>
            {
                await foreach (var widget in manager.LoadStreamAsync<TestWidget>(cancellationToken: Ct))
                    names.Add(widget.Name);
            });

            Assert.Same(midway, failure);
            Assert.Equal(["a"], names);
            Assert.NotNull(Assert.Single(provider.Contexts).DisposedAsync);
        }

        [Fact]
        public async Task ACancellationDuringTheWait_EndsTheOperation()
        {
            var (manager, _) = Stack(5, Transient(), Transient(), Transient());
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            cancel.CancelAfter(TimeSpan.FromMilliseconds(100));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.SaveAsync<TestWidget>([new TestWidget { Name = "w" }], cancel.Token));
        }
    }
}
