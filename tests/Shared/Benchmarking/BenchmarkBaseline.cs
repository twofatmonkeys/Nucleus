using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TFM.Nucleus.TestSupport.Benchmarking
{
    /// <summary>
    /// A committed baseline: what each scenario measured when it was last accepted. Written only by
    /// NUCLEUS_BENCHMARK_ACCEPT=1, so re-accepting is a deliberate change the diff shows.
    /// </summary>
    public sealed class BenchmarkBaseline
    {
        /// <summary>The commit it was accepted at (git describe, with -dirty for uncommitted work).</summary>
        public string? Commit { get; set; }

        /// <summary>The date it was accepted, as yyyy-MM-dd.</summary>
        public string? AcceptedOn { get; set; }

        /// <summary>The measures per scenario, by name.</summary>
        public SortedDictionary<string, BaselineEntry> Scenarios { get; set; } = new(StringComparer.Ordinal);

        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
        };

        /// <summary>The baseline at <paramref name="path"/>, or null when there is none.</summary>
        public static BenchmarkBaseline? Load(string path)
        {
            if (!File.Exists(path))
                return null;
            try
            {
                return JsonSerializer.Deserialize<BenchmarkBaseline>(File.ReadAllText(path), Json)
                    ?? throw new InvalidDataException($"{path} is empty.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"{path} is not a benchmark baseline: {ex.Message}", ex);
            }
        }

        /// <summary>Writes the baseline as LF-terminated UTF-8 without a BOM, scenarios sorted, so diffs stay small.</summary>
        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var text = JsonSerializer.Serialize(this, Json).Replace("\r\n", "\n") + "\n";
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        /// <summary>The working tree's commit, or null when git is not available.</summary>
        public static string? CurrentCommit()
        {
            try
            {
                using var git = Process.Start(new ProcessStartInfo("git", "describe --always --dirty")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = TestSourcePaths.ProjectDirectory(),
                });
                if (git == null)
                    return null;
                var output = git.StandardOutput.ReadToEndAsync();
                if (!git.WaitForExit(10_000))
                {
                    git.Kill();
                    return null;
                }
                var commit = output.Result.Trim();
                return git.ExitCode == 0 && commit.Length > 0 ? commit : null;
            }
            catch (Win32Exception)
            {
                return null; // git is not installed or not on the path.
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    /// <summary>One scenario's accepted measures. A measure the baseline does not gate is left out.</summary>
    public sealed class BaselineEntry
    {
        public double? MedianMs { get; set; }
        public long? AllocatedBytes { get; set; }
    }
}
