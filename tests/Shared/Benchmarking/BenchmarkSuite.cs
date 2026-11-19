using System.Globalization;

namespace TFM.Nucleus.TestSupport.Benchmarking
{
    /// <summary>
    /// Measures a list of scenarios, prints any variant comparison, and hands the baseline
    /// variant's results to <see cref="BenchmarkGate"/>. The one flow every benchmark suite runs,
    /// database or not. The caller holds <see cref="BenchmarkLock"/> for the whole suite, seeding
    /// included, so nothing else on the machine measures or loads meanwhile.
    /// </summary>
    public static class BenchmarkSuite
    {
        /// <param name="held">The machine-wide lock the caller holds; required so no suite measures without it.</param>
        /// <param name="scenarios">What to measure, in order.</param>
        /// <param name="targets">The baselines to gate against (or write, when accepting).</param>
        /// <param name="rounds">Timed runs per scenario and variant.</param>
        /// <param name="warmups">Untimed runs per scenario and variant first.</param>
        /// <param name="variants">Settings to compare; null measures the code as it is. The first is the one gated.</param>
        public static async Task MeasureAndGateAsync(BenchmarkLock held,
            IEnumerable<BenchmarkScenario> scenarios, IReadOnlyList<BaselineTarget> targets,
            int rounds, int warmups = 1, IReadOnlyList<BenchmarkVariant>? variants = null)
        {
            ArgumentNullException.ThrowIfNull(held);
            variants ??= [BenchmarkVariant.Current];
            var output = TestContext.Current.TestOutputHelper;

            var results = new List<BenchmarkResult>();
            foreach (var scenario in scenarios)
            {
                var samples = await BenchmarkRunner.MeasureAsync(scenario, variants, rounds, warmups);
                if (variants.Count > 1)
                {
                    var baseline = samples[variants[0].Name].MedianMs;
                    foreach (var variant in variants)
                    {
                        var s = samples[variant.Name];
                        output?.WriteLine(string.Create(CultureInfo.InvariantCulture,
                            $"{scenario.Name} | {variant.Name} | median {s.MedianMs:F1} ms | min {s.MinMs:F1} | max {s.MaxMs:F1} | {(s.MedianMs - baseline) / baseline * 100:+0.0;-0.0;0.0}% | {s.AllocatedBytes} B"));
                    }
                }
                var gated = variants[0];
                results.Add(new BenchmarkResult(scenario.Name, samples[gated.Name],
                    async () => (await BenchmarkRunner.MeasureAsync(scenario, [gated], rounds, warmups))[gated.Name]));
            }

            await BenchmarkGate.CheckAsync(results, targets);
        }
    }
}
