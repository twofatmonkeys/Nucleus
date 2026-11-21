using System.Text;
using System.Text.RegularExpressions;
using TFM.Nucleus.TestSupport;

namespace PortlyPrimate
{
    /// <summary>
    /// Holds the documentation to the samples. Every C# block in the README and the guide is a
    /// copy of a <c>#region</c> in this project, named by the <c>&lt;!-- sample: name --&gt;</c>
    /// line above it, so an example cannot drift from code that compiles and runs. With
    /// NUCLEUS_ACCEPT_GOLDENS=1 the accept test rewrites the blocks from the regions.
    /// </summary>
    public partial class DocSampleTests
    {
        private const string Fence = "```";

        [GeneratedRegex(@"^\s*<!--\s*sample:\s*([a-z0-9-]+)\s*-->\s*$")]
        private static partial Regex Marker();

        [GeneratedRegex(@"^\s*#region\s+([a-z0-9-]+)\s*$")]
        private static partial Regex RegionStart();

        private static string RepositoryRoot() =>
            Path.GetFullPath(Path.Combine(TestSourcePaths.ProjectDirectory(), "..", ".."));

        /// <summary>The README and every page of the guide.</summary>
        private static IEnumerable<string> Documents()
        {
            var root = RepositoryRoot();
            yield return Path.Combine(root, "README.md");
            foreach (var page in Directory.EnumerateFiles(Path.Combine(root, "docs", "guide"), "*.md").Order(StringComparer.Ordinal))
                yield return page;
        }

        /// <summary>Each region of this project's source by name, with its common indent removed.</summary>
        private static Dictionary<string, string[]> Regions()
        {
            var regions = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var project = TestSourcePaths.ProjectDirectory();
            foreach (var file in Directory.EnumerateFiles(project, "*.cs").Order(StringComparer.Ordinal))
            {
                string? name = null;
                List<string> body = [];
                foreach (var line in File.ReadAllLines(file))
                {
                    if (name is null)
                    {
                        if (RegionStart().Match(line) is { Success: true } start)
                            name = start.Groups[1].Value;
                        continue;
                    }
                    if (line.TrimStart().StartsWith("#endregion", StringComparison.Ordinal))
                    {
                        if (!regions.TryAdd(name, Dedent(body)))
                            throw new InvalidOperationException($"The region '{name}' is declared twice; the second is in {Path.GetFileName(file)}.");
                        name = null;
                        body = [];
                        continue;
                    }
                    body.Add(line);
                }
                if (name is not null)
                    throw new InvalidOperationException($"The region '{name}' in {Path.GetFileName(file)} is never closed.");
            }
            return regions;
        }

        private static string[] Dedent(List<string> lines)
        {
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0]))
                lines.RemoveAt(0);
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
                lines.RemoveAt(lines.Count - 1);
            var indent = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
            return [.. lines.Select(l => string.IsNullOrWhiteSpace(l) ? "" : l[indent..].TrimEnd())];
        }

        /// <summary>A C# block of a document: where it is, the sample it names, and its lines.</summary>
        private sealed record Block(string Document, int Line, string? Sample, int First, int Last, string[] Lines);

        private static List<Block> Blocks(string document, string[] lines)
        {
            List<Block> blocks = [];
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].TrimStart().StartsWith(Fence + "csharp", StringComparison.Ordinal))
                    continue;
                var end = Array.FindIndex(lines, i + 1, l => l.TrimStart().StartsWith(Fence, StringComparison.Ordinal));
                if (end < 0)
                    throw new InvalidOperationException($"{Path.GetFileName(document)} line {i + 1}: the code block is never closed.");
                var above = i - 1;
                while (above >= 0 && string.IsNullOrWhiteSpace(lines[above]))
                    above--;
                var sample = above >= 0 && Marker().Match(lines[above]) is { Success: true } marker ? marker.Groups[1].Value : null;
                blocks.Add(new Block(document, i + 1, sample, i + 1, end, lines[(i + 1)..end]));
                i = end;
            }
            return blocks;
        }

        private static string Name(Block block) => $"{Path.GetFileName(block.Document)} line {block.Line}";

        [Fact]
        public void EveryCodeBlock_IsACopyOfASample()
        {
            if (Environment.GetEnvironmentVariable("NUCLEUS_ACCEPT_GOLDENS") == "1")
                return; // the accept test is rewriting the blocks
            var regions = Regions();
            List<string> problems = [];
            foreach (var document in Documents())
            {
                foreach (var block in Blocks(document, File.ReadAllLines(document)))
                {
                    if (block.Sample is null)
                        problems.Add($"{Name(block)}: no <!-- sample: name --> line above the block.");
                    else if (!regions.TryGetValue(block.Sample, out var region))
                        problems.Add($"{Name(block)}: there is no region named '{block.Sample}'.");
                    else if (!region.SequenceEqual(block.Lines.Select(l => l.TrimEnd())))
                        problems.Add($"{Name(block)}: differs from the region '{block.Sample}'. Run the accept test to copy it across.");
                }
            }
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }

        [Fact]
        public void EverySample_IsShownSomewhere()
        {
            var shown = Documents().SelectMany(d => Blocks(d, File.ReadAllLines(d))).Select(b => b.Sample).ToHashSet();

            var unused = Regions().Keys.Where(name => !shown.Contains(name)).Order(StringComparer.Ordinal).ToList();

            Assert.True(unused.Count == 0, "Regions no document shows: " + string.Join(", ", unused));
        }

        [Fact]
        public void TheDocuments_NameNoExampleOutsideTheSampleDomain()
        {
            // The names the examples used before they were moved to the sample domain. One coming
            // back means an example was written by hand instead of taken from a sample.
            string[] retired = ["Customer", "Roster", "Campaign", "Voucher", "PdfTemplateFile", "Acme"];
            List<string> problems = [];
            foreach (var document in Documents())
            {
                var lines = File.ReadAllLines(document);
                for (var i = 0; i < lines.Length; i++)
                {
                    foreach (var name in retired)
                    {
                        if (Regex.IsMatch(lines[i], $@"\b{name}s?\b"))
                            problems.Add($"{Path.GetFileName(document)} line {i + 1}: '{name}'.");
                    }
                }
            }
            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        }

        [Fact]
        public void Accept_CopiesTheSamplesIntoTheDocuments()
        {
            Assert.SkipUnless(Environment.GetEnvironmentVariable("NUCLEUS_ACCEPT_GOLDENS") == "1",
                "Set NUCLEUS_ACCEPT_GOLDENS=1 to rewrite the documents' code blocks from the samples.");
            var regions = Regions();
            foreach (var document in Documents())
            {
                var text = File.ReadAllText(document);
                var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                var blocks = Blocks(document, lines);
                var output = new StringBuilder();
                var next = 0;
                foreach (var block in blocks)
                {
                    if (block.Sample is null || !regions.TryGetValue(block.Sample, out var region))
                        throw new InvalidOperationException($"{Name(block)}: it names no sample that exists, so it cannot be written.");
                    for (; next < block.First; next++)
                        output.Append(lines[next]).Append(newline);
                    foreach (var line in region)
                        output.Append(line).Append(newline);
                    next = block.Last;
                }
                for (; next < lines.Length; next++)
                {
                    output.Append(lines[next]);
                    if (next < lines.Length - 1)
                        output.Append(newline);
                }
                File.WriteAllText(document, output.ToString());
            }
        }
    }
}
