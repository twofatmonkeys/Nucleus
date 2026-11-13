using System;
using System.Text.RegularExpressions;

namespace TFM.Nucleus.Data.Queries
{
    /// <summary>What a <see cref="Measure"/> works out over the rows of each group.</summary>
    public enum MeasureKind
    {
        /// <summary>How many rows.</summary>
        Count,

        /// <summary>How many different non-null values of the column.</summary>
        CountDistinct,

        /// <summary>The smallest value of the column.</summary>
        Min,

        /// <summary>The largest value of the column.</summary>
        Max,

        /// <summary>The total of the column.</summary>
        Sum,
    }

    /// <summary>
    /// One figure a grouped row read returns beside its group keys, under <see cref="Alias"/>.
    /// <see cref="Column"/> is null only for <see cref="MeasureKind.Count"/>, which counts rows.
    /// </summary>
    public sealed record Measure
    {
        private static readonly Regex AliasPattern = new("^[A-Za-z_][A-Za-z0-9_]{0,62}$", RegexOptions.CultureInvariant);

        public Measure(MeasureKind kind, IDataColumnSchema? column, string alias)
        {
            if (kind != MeasureKind.Count && column == null)
                throw new ArgumentNullException(nameof(column), $"A {kind} measure needs a column.");
            if (!IsAlias(alias))
                throw new ArgumentException($"'{alias}' is not a usable name for a measure: start with a letter or underscore, then letters, digits or underscores, at most 63 in all.", nameof(alias));
            Kind = kind;
            Column = column;
            Alias = alias;
        }

        public MeasureKind Kind { get; }

        /// <summary>The column the measure reads, or null for a row count.</summary>
        public IDataColumnSchema? Column { get; }

        /// <summary>The name the figure is returned under. Every provider writes it quoted, and only a plain identifier is accepted.</summary>
        public string Alias { get; }

        /// <summary>
        /// The schema the figure converts by when read: a minimum or maximum is the column's own
        /// type, so a date stays a date; a count or a sum is the number the engine returns.
        /// </summary>
        public IDataColumnSchema? ValueColumn => Kind is MeasureKind.Min or MeasureKind.Max ? Column : null;

        /// <summary>True when <paramref name="alias"/> is a plain identifier every provider can quote.</summary>
        public static bool IsAlias(string? alias) => alias != null && AliasPattern.IsMatch(alias);
    }

    /// <summary>
    /// One value of each row a row read returns: its name, and the column whose type it converts
    /// by, or null when it is returned as the engine gives it.
    /// </summary>
    public sealed record ResultColumn(string Name, IDataColumnSchema? Column);
}
