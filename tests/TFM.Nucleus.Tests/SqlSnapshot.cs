using System.Text.RegularExpressions;

namespace TFM.Nucleus.Tests
{
    /// <summary>
    /// Normalizes generated SQL so snapshots are deterministic: parameter variable names are
    /// random per <see cref="Parameter"/> instance, so each distinct @token is replaced with
    /// @p0, @p1, ... in order of first appearance. Whitespace runs collapse to single spaces.
    /// </summary>
    public static partial class SqlSnapshot
    {
        [GeneratedRegex(@"@[A-Za-z0-9]+")]
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
    }
}
