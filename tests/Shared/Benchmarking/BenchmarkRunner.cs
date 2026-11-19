using System.Diagnostics;

namespace TFM.Nucleus.TestSupport.Benchmarking
{
    /// <summary>
    /// Runs a scenario: untimed warm-ups, then timed rounds. With several variants the rounds are
    /// interleaved, alternating which variant goes first, so drift in the server or the network
    /// falls on every variant alike.
    /// </summary>
    public static class BenchmarkRunner
    {
        /// <summary>Measures <paramref name="scenario"/> under each variant.</summary>
        /// <param name="scenario">What to time.</param>
        /// <param name="variants">The settings compared. The first is the one a baseline records.</param>
        /// <param name="rounds">Timed runs per variant.</param>
        /// <param name="warmups">Untimed runs per variant first, so the timed runs see jitted, tiered-up code.</param>
        public static async Task<IReadOnlyDictionary<string, BenchmarkSample>> MeasureAsync(
            BenchmarkScenario scenario, IReadOnlyList<BenchmarkVariant> variants, int rounds, int warmups = 1)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            if (variants.Count == 0)
                throw new ArgumentException("At least one variant is needed.", nameof(variants));
            ArgumentOutOfRangeException.ThrowIfLessThan(rounds, 1);

            var times = variants.ToDictionary(v => v.Name, _ => new List<double>());
            var bytes = variants.ToDictionary(v => v.Name, _ => new List<long>());
            if (scenario.Seed != null)
                await scenario.Seed();
            try
            {
                for (var w = 0; w < warmups; w++)
                    foreach (var variant in variants)
                        await RunOnceAsync(scenario, variant);
                for (var round = 0; round < rounds; round++)
                {
                    foreach (var variant in round % 2 == 0 ? variants : variants.Reverse())
                    {
                        var (ms, allocated) = await RunOnceAsync(scenario, variant);
                        times[variant.Name].Add(ms);
                        bytes[variant.Name].Add(allocated);
                    }
                }
            }
            finally
            {
                if (scenario.TearDown != null)
                    await scenario.TearDown();
            }

            return variants.ToDictionary(
                v => v.Name,
                v => new BenchmarkSample(Median(times[v.Name]), times[v.Name].Min(), times[v.Name].Max(), (long)Median(bytes[v.Name].Select(b => (double)b))));
        }

        /// <summary>One run: prepare, then the timed work, then reset, the last even when the run fails.</summary>
        private static async Task<(double Ms, long Allocated)> RunOnceAsync(BenchmarkScenario scenario, BenchmarkVariant variant)
        {
            variant.Apply();
            if (scenario.Prepare != null)
                await scenario.Prepare();
            try
            {
                // Process-wide and precise, so work the run hands to other threads (a parallel
                // query build, a driver's I/O completion) counts too. The benchmarks hold
                // BenchmarkLock and run unparallelised, so no other test allocates meanwhile.
                var before = GC.GetTotalAllocatedBytes(precise: true);
                var watch = Stopwatch.StartNew();
                await scenario.Run();
                watch.Stop();
                return (watch.Elapsed.TotalMilliseconds, GC.GetTotalAllocatedBytes(precise: true) - before);
            }
            finally
            {
                if (scenario.Reset != null)
                    await scenario.Reset();
            }
        }

        /// <summary>The median of <paramref name="values"/>; the mean of the middle two for an even count.</summary>
        public static double Median(IEnumerable<double> values)
        {
            var sorted = values.Order().ToList();
            if (sorted.Count == 0)
                throw new ArgumentException("No values.", nameof(values));
            var mid = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
        }
    }
}
