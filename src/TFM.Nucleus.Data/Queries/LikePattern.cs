using System.Text;

namespace TFM.Nucleus.Data.Queries
{
    /// <summary>
    /// A pattern term (StartsWith, EndsWith, Like and the case-sensitive Contains) matches its
    /// value literally: a % or _ in the value is that character, not a wildcard. The value is
    /// bound with each character the engines treat as special preceded by
    /// <see cref="EscapeCharacter"/>, and every renderer names that character as its pattern
    /// escape. [ is escaped too, which SQL Server reads as the start of a character class and
    /// PostgreSQL as itself.
    /// </summary>
    public static class LikePattern
    {
        /// <summary>The escape character every renderer declares for a pattern term.</summary>
        public const char EscapeCharacter = '\\';

        /// <summary>True for an operator a renderer writes as a pattern match.</summary>
        public static bool Applies(Operator op) =>
            op is Operator.Like or Operator.LikeCaseSensitive or Operator.StartsWith or Operator.EndsWith or Operator.ContainsCaseSensitive;

        /// <summary>The value with every special character escaped.</summary>
        public static string Escape(string value)
        {
            var sb = new StringBuilder(value.Length + 4);
            foreach (var c in value)
            {
                if (c is EscapeCharacter or '%' or '_' or '[')
                    sb.Append(EscapeCharacter);
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
