using TFM.Nucleus.TestSupport.Benchmarking;

namespace TFM.Nucleus.IntegrationTests.Benchmarks
{
    /// <summary>
    /// What every database benchmark suite shares: the NUCLEUS_BENCHMARK switch, the locks, and a
    /// baseline per machine, provider, framework and configuration that gates both time and
    /// allocations. Each suite is abstract with a sealed subclass per provider, like the tests.
    /// </summary>
    public abstract class DatabaseBenchmarks(DatabaseFixture fixture)
    {
        /// <summary>Timed runs per scenario. A median of 15 rides out the odd slow round trip.</summary>
        protected const int Rounds = 15;

        protected static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static int _suitesInProcess;

        /// <summary>The settings compared. The first is the one the baseline records and gates.</summary>
        protected virtual IReadOnlyList<BenchmarkVariant> Variants => [BenchmarkVariant.Current];

        /// <summary>
        /// The start of every benchmark row's name on this machine. The machine is part of it
        /// because a database (the SQL Server one, say) can be shared between machines, and a
        /// purge must never delete the rows of another machine's run in progress.
        /// </summary>
        private static readonly string RowPrefix = $"bench-{BenchmarkSettings.Machine}-";

        /// <summary>A prefix unique to one run, so its rows are found and deleted by name.</summary>
        protected static string Prefix() => $"{RowPrefix}{Guid.NewGuid():N}-";

        /// <summary>
        /// Deletes this machine's benchmark rows, before measuring. A run that failed or was
        /// stopped leaves its rows behind, and the scenarios filter by name without an index, so
        /// leftover rows would slow every later run and show up as a regression that is not one.
        /// </summary>
        private static async Task PurgeAsync(ObjectDataManager manager)
        {
            await manager.DeleteWhereAsync<IntBenchWidget>(w => w.Name!.StartsWith(RowPrefix), Ct);
            await manager.DeleteWhereAsync<IntBenchOrder>(o => o.Name!.StartsWith(RowPrefix), Ct);
            await manager.DeleteWhereAsync<IntBenchLazyOwner>(o => o.Name!.StartsWith(RowPrefix), Ct);
            await manager.DeleteWhereAsync<IntBenchNestOrder>(o => o.Name!.StartsWith(RowPrefix), Ct);
        }

        /// <summary>
        /// Runs <paramref name="suite"/>'s scenarios and gates them. <paramref name="seed"/> writes
        /// the data every scenario reads and returns how to delete it; it runs under the locks but
        /// outside the timing.
        /// </summary>
        protected async Task RunAsync(string suite, Func<ObjectDataManager, IEnumerable<BenchmarkScenario>> scenarios,
            Func<ObjectDataManager, Task<Func<Task>>>? seed = null)
        {
            Assert.SkipUnless(BenchmarkSettings.Enabled, "Set NUCLEUS_BENCHMARK=1 to run the database benchmarks.");
            var manager = fixture.RequireManager();
            // Allocations are counted for the whole process, and after another suite's heavy work
            // the same scenario allocates 15-20% more, so each suite gets a process of its own.
            Assert.SkipWhen(Interlocked.Increment(ref _suitesInProcess) > 1,
                "Another database benchmark suite already ran in this process, which would skew this one's allocations. " +
                "Run tools/Run-Benchmarks.ps1, which gives each suite its own process.");
            // One lock for every benchmark on the machine. It also keeps the net8.0 and net10.0
            // runs, which share each database, out of each other's rows. A per-provider test lock
            // as well only made the other run wait on it for a whole suite and time out.
            using var machineLock = await BenchmarkLock.AcquireAsync(cancellationToken: Ct);
            await PurgeAsync(manager);
            await fixture.SettleAsync(Ct);
            var cleanup = seed == null ? null : await seed(manager);
            try
            {
                await BenchmarkSuite.MeasureAndGateAsync(machineLock, scenarios(manager),
                    [BaselineTarget.ForMachine($"{suite}.{fixture.ProviderName}", BenchmarkMeasure.Time | BenchmarkMeasure.Allocations)],
                    Rounds, warmups: 1, Variants);
            }
            finally
            {
                if (cleanup != null)
                    await cleanup();
            }
        }
    }
}
