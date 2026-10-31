using System.Text.RegularExpressions;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// Normalizes generated SQL so snapshots are deterministic: parameter variable names are
    /// random per <see cref="Parameter"/> instance, so each distinct @token is replaced with
    /// @p0, @p1, ... in order of first appearance. System functions such as @@ROWCOUNT are left
    /// alone. Whitespace runs collapse to single spaces.
    /// </summary>
    public static partial class SqlSnapshot
    {
        [GeneratedRegex(@"(?<!@)@(?!@)[A-Za-z0-9]+")]
        private static partial Regex VariableRegex();

        [GeneratedRegex(@"\s+")]
        private static partial Regex WhitespaceRegex();

        public static string Normalize(string sql)
        {
            var map = new Dictionary<string, string>();
            var replaced = VariableRegex().Replace(sql, m =>
            {
                if (!map.TryGetValue(m.Value, out var name))
                {
                    name = $"@p{map.Count}";
                    map[m.Value] = name;
                }
                return name;
            });
            return WhitespaceRegex().Replace(replaced, " ").Trim();
        }

        // Only the generated p{n} variables are renumbered: as a parameter (@p12) or as the
        // alias an output column is returned under ([p12] on SQL Server, "p12" on PostgreSQL).
        // Named variables such as @sql or @tempTableName in the schema scripts are part of the
        // text and stay as written.
        [GeneratedRegex(@"(?<=(?<!@)@|\[|"")p\d+(?!\d)")]
        private static partial Regex GeneratedVariableRegex();

        /// <summary>
        /// Renumbers the generated variables and turns CRLF into LF, and changes nothing else, so
        /// a raw golden pins every space and separator. <see cref="Normalize"/> collapses
        /// whitespace, which hides exactly the differences a renderer refactor can introduce.
        /// </summary>
        public static string NormalizeVariablesOnly(string sql)
        {
            var map = new Dictionary<string, string>();
            var replaced = GeneratedVariableRegex().Replace(sql, m =>
            {
                if (!map.TryGetValue(m.Value, out var name))
                {
                    name = $"p{map.Count}";
                    map[m.Value] = name;
                }
                return name;
            });
            return replaced.Replace("\r\n", "\n");
        }
    }
}
