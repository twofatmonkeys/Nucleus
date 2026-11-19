using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using TFM.Nucleus.Data.Queries;

namespace TFM.Nucleus.Data.SQLite
{
    /// <summary>
    /// The functions Nucleus registers on every SQLite connection it opens, and that its queries
    /// call. SQLite's own LIKE and lower() fold the case of ASCII letters only, and it has no
    /// case-sensitive LIKE for one expression, so the pattern terms match here instead, folding
    /// case across Unicode as SQL Server and PostgreSQL do. Its own clock and random functions
    /// write text in no form Nucleus stores, so the server functions run here too, writing the
    /// forms <see cref="SqliteValues"/> binds.
    /// <para>
    /// A query Nucleus writes therefore runs only on a connection Nucleus opened. The data does
    /// not depend on them: any SQLite tool reads and writes the file.
    /// </para>
    /// </summary>
    internal static class SqliteFunctions
    {
        /// <summary>LIKE without regard to case: <c>nucleus_like(value, pattern)</c>.</summary>
        public const string Like = "nucleus_like";

        /// <summary>LIKE with regard to case: <c>nucleus_like_cs(value, pattern)</c>.</summary>
        public const string LikeCaseSensitive = "nucleus_like_cs";

        /// <summary>The value in lower case, across Unicode: <c>nucleus_lower(value)</c>.</summary>
        public const string Lower = "nucleus_lower";

        /// <summary>True when the value holds the term, without regard to case: <c>nucleus_contains(value, term)</c>.</summary>
        public const string Contains = "nucleus_contains";

        public const string NowOffset = "nucleus_now_offset";
        public const string NowLocal = "nucleus_now_local";
        public const string NowUtc = "nucleus_now_utc";
        public const string NewGuid = "nucleus_new_guid";

        /// <summary>Registers every function on <paramref name="connection"/>; the driver keeps them across opens.</summary>
        public static void Register(SqliteConnection connection)
        {
            connection.CreateFunction<string?, string?, long?>(Like, (value, pattern) => Match(value, pattern, caseSensitive: false), isDeterministic: true);
            connection.CreateFunction<string?, string?, long?>(LikeCaseSensitive, (value, pattern) => Match(value, pattern, caseSensitive: true), isDeterministic: true);
            connection.CreateFunction<string?, string?>(Lower, value => value?.ToLowerInvariant(), isDeterministic: true);
            connection.CreateFunction<string?, string?, long?>(Contains,
                (value, term) => value == null || term == null ? null : value.Contains(term, StringComparison.OrdinalIgnoreCase) ? 1 : 0,
                isDeterministic: true);
            connection.CreateFunction(NowOffset, () => SqliteValues.FormatDateTimeOffset(DateTimeOffset.UtcNow), isDeterministic: false);
            connection.CreateFunction(NowLocal, () => SqliteValues.FormatDateTime(DateTime.Now), isDeterministic: false);
            connection.CreateFunction(NowUtc, () => SqliteValues.FormatDateTime(DateTime.UtcNow), isDeterministic: false);
            connection.CreateFunction(NewGuid, () => SqliteValues.FormatGuid(Guid.NewGuid()), isDeterministic: false);
        }

        private static long? Match(string? value, string? pattern, bool caseSensitive) =>
            value == null || pattern == null ? null : IsLike(value, pattern, caseSensitive) ? 1 : 0;

        /// <summary>
        /// SQL LIKE: % matches any run of characters, _ any one, and
        /// <see cref="LikePattern.EscapeCharacter"/> makes the next character literal, which is how
        /// a bound pattern value arrives. Without <paramref name="caseSensitive"/>, characters
        /// compare as their invariant upper case, as <see cref="StringComparison.OrdinalIgnoreCase"/> does.
        /// </summary>
        public static bool IsLike(string value, string pattern, bool caseSensitive)
        {
            var tokens = Tokenize(pattern);
            int v = 0, t = 0, starToken = -1, starValue = 0;
            while (v < value.Length)
            {
                if (t < tokens.Count && (tokens[t].Kind == TokenKind.One || (tokens[t].Kind == TokenKind.Literal && Same(tokens[t].Char, value[v], caseSensitive))))
                {
                    v++;
                    t++;
                }
                else if (t < tokens.Count && tokens[t].Kind == TokenKind.Many)
                {
                    starToken = t++;
                    starValue = v;
                }
                else if (starToken >= 0)
                {
                    // Let the last % take one more character, and try the rest again from there.
                    t = starToken + 1;
                    v = ++starValue;
                }
                else
                    return false;
            }
            while (t < tokens.Count && tokens[t].Kind == TokenKind.Many)
                t++;
            return t == tokens.Count;
        }

        private static bool Same(char a, char b, bool caseSensitive) =>
            a == b || (!caseSensitive && char.ToUpperInvariant(a) == char.ToUpperInvariant(b));

        private enum TokenKind { Literal, One, Many }

        private readonly record struct Token(TokenKind Kind, char Char);

        private static List<Token> Tokenize(string pattern)
        {
            List<Token> tokens = new(pattern.Length);
            for (var i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];
                if (c == LikePattern.EscapeCharacter && i + 1 < pattern.Length)
                    tokens.Add(new Token(TokenKind.Literal, pattern[++i]));
                else if (c == '%')
                {
                    // Runs of % mean what one does.
                    if (tokens.Count == 0 || tokens[^1].Kind != TokenKind.Many)
                        tokens.Add(new Token(TokenKind.Many, c));
                }
                else if (c == '_')
                    tokens.Add(new Token(TokenKind.One, c));
                else
                    tokens.Add(new Token(TokenKind.Literal, c));
            }
            return tokens;
        }
    }
}
