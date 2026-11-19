namespace TFM.Nucleus.TestSupport.Benchmarking
{
    /// <summary>
    /// One thing a benchmark times. Only <see cref="Run"/> is timed; the other steps set up and
    /// clean up around it, so every timed run starts from the same state.
    /// </summary>
    public sealed class BenchmarkScenario
    {
        /// <summary>The name the baseline records it under. Renaming it needs a new baseline.</summary>
        public required string Name { get; init; }

        /// <summary>Once, before the first run: data every run reads.</summary>
        public Func<Task>? Seed { get; init; }

        /// <summary>Before every run: the objects the run writes.</summary>
        public Func<Task>? Prepare { get; init; }

        /// <summary>The timed work.</summary>
        public required Func<Task> Run { get; init; }

        /// <summary>After every run: removes what the run wrote.</summary>
        public Func<Task>? Reset { get; init; }

        /// <summary>Once, after the last run, even when a run failed: removes what Seed wrote.</summary>
        public Func<Task>? TearDown { get; init; }

        /// <summary>A scenario of synchronous work, for the in-process benchmarks.</summary>
        public static BenchmarkScenario Of(string name, Action run, Action? prepare = null) => new()
        {
            Name = name,
            Prepare = prepare == null ? null : () => { prepare(); return Task.CompletedTask; },
            Run = () => { run(); return Task.CompletedTask; },
        };
    }

    /// <summary>A setting a benchmark compares, applied before each timed run.</summary>
    public sealed record BenchmarkVariant(string Name, Action Apply)
    {
        /// <summary>The code as it is, with nothing changed: the variant the baseline records.</summary>
        public static readonly BenchmarkVariant Current = new("current", () => { });
    }

    /// <summary>What one scenario measured under one variant, over every timed run.</summary>
    /// <param name="MedianMs">The median wall time of a run.</param>
    /// <param name="MinMs">The fastest run.</param>
    /// <param name="MaxMs">The slowest run.</param>
    /// <param name="AllocatedBytes">The median bytes the process allocated during a run.</param>
    public sealed record BenchmarkSample(double MedianMs, double MinMs, double MaxMs, long AllocatedBytes);
}
