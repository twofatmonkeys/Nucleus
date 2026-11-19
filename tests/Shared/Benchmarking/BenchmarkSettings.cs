using System.Diagnostics;
using System.Globalization;

namespace TFM.Nucleus.TestSupport.Benchmarking
{
    /// <summary>The benchmark switches, tolerances and the key a baseline is filed under.</summary>
    public static class BenchmarkSettings
    {
        /// <summary>NUCLEUS_BENCHMARK=1 runs the benchmarks. Without it every benchmark skips, CI included.</summary>
        public static bool Enabled => Environment.GetEnvironmentVariable("NUCLEUS_BENCHMARK") == "1";

        /// <summary>NUCLEUS_BENCHMARK_ACCEPT=1 writes the baselines instead of gating against them.</summary>
        public static bool Accept => Environment.GetEnvironmentVariable("NUCLEUS_BENCHMARK_ACCEPT") == "1";

        /// <summary>
        /// How far a median time may rise above its baseline before the table flags it, in
        /// percent: NUCLEUS_BENCHMARK_TOLERANCE, else 20. Time is reported, never gated.
        /// </summary>
        public static double TimeTolerancePercent
        {
            get
            {
                var raw = Environment.GetEnvironmentVariable("NUCLEUS_BENCHMARK_TOLERANCE");
                if (string.IsNullOrWhiteSpace(raw))
                    return 20;
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) && percent > 0)
                    return percent;
                throw new InvalidOperationException($"NUCLEUS_BENCHMARK_TOLERANCE must be a positive number of percent, not '{raw}'.");
            }
        }

        /// <summary>A time change smaller than this is never flagged, whatever its percentage: timer and scheduler noise.</summary>
        public const double TimeFloorMs = 2;

        /// <summary>How far allocations may rise above their baseline before they fail, in percent.</summary>
        public const double AllocationTolerancePercent = 10;

        /// <summary>An allocation rise smaller than this never fails, whatever its percentage.</summary>
        public const long AllocationFloorBytes = 4096;

        /// <summary>The machine a timing baseline belongs to.</summary>
        public static string Machine => Environment.MachineName.ToLowerInvariant();

        /// <summary>The target framework this test assembly was built for.</summary>
        public static string Framework =>
#if NET10_0
            "net10.0";
#elif NET8_0
            "net8.0";
#else
            throw new PlatformNotSupportedException("Add this target framework to BenchmarkSettings.Framework.");
#endif

        /// <summary>
        /// True when the Nucleus assemblies under test were built optimised. Debug code allocates
        /// differently (no escape analysis, no inlining), so only optimised builds are gated.
        /// </summary>
        public static bool IsOptimized =>
            typeof(CoreObject).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false)
                .OfType<DebuggableAttribute>()
                .All(a => !a.IsJITOptimizerDisabled);

        /// <summary>The build configuration, as a baseline's file name records it.</summary>
        public static string Configuration => IsOptimized ? "Release" : "Debug";
    }
}
