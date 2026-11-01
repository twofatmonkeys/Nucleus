using System.Diagnostics;

namespace TFM.Nucleus.IntegrationTests.Benchmarks
{
    /// <summary>
    /// Save throughput on each write path: one object at a time, a batch in one command, a batch
    /// split across commands (the sync path), and a batch large enough for the bulk engine. Each
    /// scenario inserts its rows and then updates them. Skipped unless NUCLEUS_BENCHMARK=1.
    /// <para>
    /// Variants are measured interleaved, alternating which goes first each round, so drift in
    /// the server or the network falls on both alike. Rows are deleted after every run, outside
    /// the timing, so every run writes to a table of the same size.
    /// </para>
    /// </summary>
    public abstract class SaveBenchmarks(DatabaseFixture fixture)
    {
        private const int Rounds = 15;

        /// <summary>A setting a benchmark compares, applied before each timed run.</summary>
        public sealed record Variant(string Name, Action Apply);

        /// <summary>The variants to compare. The first is the baseline the others are measured against.</summary>
        protected virtual IReadOnlyList<Variant> Variants => [new("current", () => { })];

        private sealed record Scenario(string Name, Func<string, Task> Run);

        private static IEnumerable<Scenario> Scenarios(ObjectDataManager manager) =>
        [
            new("single x20", async prefix =>
            {
                var widgets = New(prefix, 20);
                foreach (var w in widgets)
                    await manager.SaveAsync(w);
                foreach (var w in widgets)
                {
                    w.Quantity++;
                    await manager.SaveAsync(w);
                }
            }),
            new("batch 100", async prefix =>
            {
                var widgets = New(prefix, 100);
                await manager.SaveAsync(widgets);
                widgets.ForEach(w => w.Quantity++);
                await manager.SaveAsync(widgets);
            }),
            new("sync split 1000", prefix =>
            {
                var widgets = New(prefix, 1000);
                manager.Save(widgets);
                widgets.ForEach(w => w.Quantity++);
                manager.Save(widgets);
                return Task.CompletedTask;
            }),
            new("bulk 20000", async prefix =>
            {
                var widgets = New(prefix, 20000);
                await manager.SaveAsync(widgets);
                widgets.ForEach(w => w.Quantity++);
                await manager.SaveAsync(widgets);
            }),
        ];

        private static List<IntBenchWidget> New(string prefix, int count) =>
            [.. Enumerable.Range(0, count).Select(i => new IntBenchWidget { Name = $"{prefix}{i}", Quantity = i })];

        [Fact]
        public async Task SaveScenarios()
        {
            Assert.SkipUnless(Environment.GetEnvironmentVariable("NUCLEUS_BENCHMARK") == "1",
                "Set NUCLEUS_BENCHMARK=1 to run the save benchmarks.");
            var manager = fixture.RequireManager();
            using var gate = await fixture.LockAsync("benchmarks");
            var output = TestContext.Current.TestOutputHelper;
            var variants = Variants;

            foreach (var scenario in Scenarios(manager))
            {
                var samples = variants.ToDictionary(v => v.Name, _ => new List<double>());
                foreach (var variant in variants)
                    await RunOnce(manager, scenario, variant);
                for (var round = 0; round < Rounds; round++)
                    foreach (var variant in round % 2 == 0 ? variants : variants.Reverse())
                        samples[variant.Name].Add(await RunOnce(manager, scenario, variant));

                var baseline = Median(samples[variants[0].Name]);
                foreach (var variant in variants)
                {
                    var median = Median(samples[variant.Name]);
                    var overhead = (median - baseline) / baseline * 100;
                    output?.WriteLine($"{fixture.ProviderName} | {scenario.Name} | {variant.Name} | median {median:F1} ms | min {samples[variant.Name].Min():F1} | max {samples[variant.Name].Max():F1} | {overhead:+0.0;-0.0;0.0}%");
                }
            }
        }

        /// <summary>Times one run of a scenario under a variant, then deletes its rows untimed.</summary>
        private static async Task<double> RunOnce(ObjectDataManager manager, Scenario scenario, Variant variant)
        {
            var prefix = $"bench-{Guid.NewGuid():N}-";
            variant.Apply();
            var watch = Stopwatch.StartNew();
            await scenario.Run(prefix);
            watch.Stop();
            await manager.DeleteWhereAsync<IntBenchWidget>(w => w.Name!.StartsWith(prefix), TestContext.Current.CancellationToken);
            return watch.Elapsed.TotalMilliseconds;
        }

        private static double Median(List<double> values)
        {
            var sorted = values.Order().ToList();
            var mid = sorted.Count / 2;
            return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
        }
    }
}
