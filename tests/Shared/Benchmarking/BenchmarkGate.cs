using System.Globalization;
using System.Text;

namespace TFM.Nucleus.TestSupport.Benchmarking
{
    /// <summary>The measures a baseline file records and gates.</summary>
    [Flags]
    public enum BenchmarkMeasure
    {
        Time = 1,
        Allocations = 2,
    }

    /// <summary>
    /// A baseline file and what it gates. Timings depend on the machine and its database, so they
    /// are filed per machine; allocations do not, so they are filed once for every machine.
    /// </summary>
    /// <param name="RelativePath">The file, under the test project's source folder.</param>
    /// <param name="Measures">The measures it records and gates.</param>
    public sealed record BaselineTarget(string RelativePath, BenchmarkMeasure Measures)
    {
        /// <summary>A baseline for this machine: Benchmarks/Baselines/&lt;machine&gt;/&lt;suite&gt;.&lt;framework&gt;.&lt;configuration&gt;.json.</summary>
        public static BaselineTarget ForMachine(string suite, BenchmarkMeasure measures) =>
            new(FileFor(BenchmarkSettings.Machine, suite), measures);

        /// <summary>A baseline for every machine: Benchmarks/Baselines/any/&lt;suite&gt;.&lt;framework&gt;.&lt;configuration&gt;.json.</summary>
        public static BaselineTarget ForAnyMachine(string suite, BenchmarkMeasure measures) =>
            new(FileFor("any", suite), measures);

        private static string FileFor(string folder, string suite) =>
            Path.Combine("Benchmarks", "Baselines", folder, $"{suite}.{BenchmarkSettings.Framework}.{BenchmarkSettings.Configuration}.json");
    }

    /// <summary>One scenario's measurement, with the way to measure it again.</summary>
    public sealed record BenchmarkResult(string Scenario, BenchmarkSample Sample, Func<Task<BenchmarkSample>> Remeasure);

    /// <summary>
    /// Compares a run with its baselines and fails on an allocation regression, or writes the
    /// baselines when NUCLEUS_BENCHMARK_ACCEPT=1. Allocations repeat to within about 1% run to
    /// run, so they are gated: a scenario fails when it allocates more than its baseline by more
    /// than both the tolerance and the floor, and still does when it is measured once more. Time
    /// is reported with its delta but never fails: on a development database the same code
    /// measured 7 ms and 13 ms in back-to-back runs, which no stored baseline can gate. A scenario
    /// with no baseline fails, as a missing golden does. Getting better never fails; the table
    /// suggests accepting it.
    /// </summary>
    public static class BenchmarkGate
    {
        private sealed record Row(string Scenario, string Measure, string Baseline, string Now, string Delta, string Verdict, bool Failed);

        public static async Task CheckAsync(IReadOnlyList<BenchmarkResult> results, IReadOnlyList<BaselineTarget> targets)
        {
            var output = TestContext.Current.TestOutputHelper;
            var root = TestSourcePaths.ProjectDirectory();

            if (BenchmarkSettings.Accept)
            {
                foreach (var target in targets)
                {
                    var baseline = new BenchmarkBaseline
                    {
                        Commit = BenchmarkBaseline.CurrentCommit(),
                        AcceptedOn = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    };
                    foreach (var result in results)
                        baseline.Scenarios[result.Scenario] = new BaselineEntry
                        {
                            MedianMs = target.Measures.HasFlag(BenchmarkMeasure.Time) ? Math.Round(result.Sample.MedianMs, 2) : null,
                            AllocatedBytes = target.Measures.HasFlag(BenchmarkMeasure.Allocations) ? result.Sample.AllocatedBytes : null,
                        };
                    baseline.Save(Path.Combine(root, target.RelativePath));
                    output?.WriteLine($"Accepted {target.RelativePath}");
                }
                output?.WriteLine(Table(results.SelectMany(r => Rows(r.Scenario, r.Sample, null, targets.Aggregate((BenchmarkMeasure)0, (m, t) => m | t.Measures)))));
                return;
            }

            var baselines = targets.Select(t => (Target: t, Baseline: BenchmarkBaseline.Load(Path.Combine(root, t.RelativePath)))).ToList();
            var rows = new List<Row>();
            foreach (var result in results)
            {
                var sample = result.Sample;
                var scenarioRows = Compare(result.Scenario, sample, baselines);
                if (scenarioRows.Any(r => r.Failed) && scenarioRows.All(r => r.Baseline != "none"))
                {
                    // Over budget once: measure again before calling it a regression.
                    var again = await result.Remeasure();
                    var retried = Compare(result.Scenario, again, baselines);
                    var first = scenarioRows;
                    scenarioRows = retried.Any(r => r.Failed)
                        ? retried.Select(r => r with { Verdict = r.Failed ? $"{r.Verdict} (twice; first {Format(r.Measure, first.First(s => s.Measure == r.Measure).Now)})" : r.Verdict }).ToList()
                        : retried.Select(r => r.Measure == "alloc" ? r with { Verdict = "ok on re-measure" } : r).ToList();
                }
                rows.AddRange(scenarioRows);
            }

            var table = Table(rows);
            output?.WriteLine(table);
            var stale = baselines
                .Where(b => b.Baseline != null)
                .SelectMany(b => b.Baseline!.Scenarios.Keys.Except(results.Select(r => r.Scenario)).Select(s => $"{s} in {b.Target.RelativePath}"))
                .ToList();
            if (stale.Count > 0)
                output?.WriteLine($"Baseline scenarios no longer measured (accept to drop them): {string.Join(", ", stale)}");

            if (rows.Any(r => r.Failed))
                Assert.Fail($"Benchmark regression or missing baseline.\n{table}\nA deliberate change is accepted with NUCLEUS_BENCHMARK_ACCEPT=1, with the reason in the commit message.");
        }

        private static List<Row> Compare(string scenario, BenchmarkSample sample, List<(BaselineTarget Target, BenchmarkBaseline? Baseline)> baselines)
        {
            var rows = new List<Row>();
            foreach (var (target, baseline) in baselines)
            {
                var entry = baseline?.Scenarios.GetValueOrDefault(scenario);
                if (entry == null)
                {
                    rows.AddRange(Rows(scenario, sample, null, target.Measures)
                        .Select(r => r with { Verdict = $"no baseline in {target.RelativePath}: run with NUCLEUS_BENCHMARK_ACCEPT=1", Failed = true }));
                    continue;
                }
                rows.AddRange(Rows(scenario, sample, entry, target.Measures));
            }
            return rows;
        }

        private static IEnumerable<Row> Rows(string scenario, BenchmarkSample sample, BaselineEntry? entry, BenchmarkMeasure measures)
        {
            if (measures.HasFlag(BenchmarkMeasure.Time))
                yield return Judge(scenario, "time", entry?.MedianMs, sample.MedianMs,
                    BenchmarkSettings.TimeTolerancePercent, BenchmarkSettings.TimeFloorMs, gated: false);
            if (measures.HasFlag(BenchmarkMeasure.Allocations))
                yield return Judge(scenario, "alloc", entry?.AllocatedBytes, sample.AllocatedBytes,
                    BenchmarkSettings.AllocationTolerancePercent, BenchmarkSettings.AllocationFloorBytes, gated: true);
        }

        private static Row Judge(string scenario, string measure, double? baseline, double now, double tolerancePercent, double floor, bool gated)
        {
            if (baseline is not { } b)
                return new Row(scenario, measure, "none", Raw(measure, now), "", "", false);
            var delta = b == 0 ? 0 : (now - b) / b * 100;
            var verdict = "ok";
            var failed = false;
            if (now > b * (1 + tolerancePercent / 100) && now - b > floor)
            {
                verdict = gated ? $"REGRESSION (over {tolerancePercent:0.#}%)" : $"slower by {delta:0}% (reported, not gated)";
                failed = gated;
            }
            else if (now < b * (1 - tolerancePercent / 100) && b - now > floor)
                verdict = gated ? $"down by {-delta:0}%: consider accepting" : $"faster by {-delta:0}%";
            return new Row(scenario, measure, Raw(measure, b), Raw(measure, now), $"{delta:+0.0;-0.0;0.0}%", verdict, failed);
        }

        private static string Raw(string measure, double value) =>
            measure == "time" ? value.ToString("F1", CultureInfo.InvariantCulture) : ((long)value).ToString(CultureInfo.InvariantCulture);

        private static string Format(string measure, string value) => measure == "time" ? $"{value} ms" : $"{value} B";

        private static string Table(IEnumerable<Row> rows)
        {
            var list = rows.ToList();
            string[] headers = ["scenario", "measure", "baseline", "now", "delta", "verdict"];
            string[][] cells = [.. list.Select(r => new[] { r.Scenario, r.Measure, r.Baseline, r.Now, r.Delta, r.Verdict })];
            var widths = headers.Select((h, i) => Math.Max(h.Length, cells.Length == 0 ? 0 : cells.Max(c => c[i].Length))).ToArray();
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(" | ", headers.Select((h, i) => h.PadRight(widths[i]))).TrimEnd());
            sb.AppendLine(string.Join("-|-", widths.Select(w => new string('-', w))));
            foreach (var c in cells)
                sb.AppendLine(string.Join(" | ", c.Select((v, i) => v.PadRight(widths[i]))).TrimEnd());
            return sb.ToString();
        }
    }
}
