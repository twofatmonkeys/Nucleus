using System;

namespace TFM.Nucleus.Data.Schema
{
    /// <summary>
    /// How a provider makes table names from entity type names: <see cref="FullName"/>, which is
    /// what every table was named before policies existed, or <see cref="ObjectName"/>, which keeps
    /// only the last parts of the full name. An entity overrides its provider's policy with
    /// <see cref="TableNamingAttribute"/>, and both with <see cref="DatabaseTableAttribute"/>.
    /// </summary>
    /// <remarks>
    /// A policy only names tables. Changing it on a database that already has tables makes
    /// Nucleus look for tables under the new names, so set the provider's
    /// RenameFullNameTables, or list the old names in <see cref="LegacyTableNamesAttribute"/>,
    /// or the old tables are left behind and new empty ones created.
    /// </remarks>
    public sealed class TableNamingPolicy : IEquatable<TableNamingPolicy>
    {
        /// <summary>The prefix every policy-made table name starts with.</summary>
        public const string Prefix = "tbl_";

        private static readonly char[] Separators = ['.', '+'];

        private TableNamingPolicy(TableNamingStyle style, int levels)
        {
            Style = style;
            Levels = levels;
        }

        /// <summary>tbl_ and the type's full name with its separators replaced: tbl_TFM_Nucleus_Bindery_BinderyDocument.</summary>
        public static TableNamingPolicy FullName { get; } = new(TableNamingStyle.FullName, 0);

        /// <summary>
        /// tbl_ and the last <paramref name="levels"/> parts of the type's full name, split at each
        /// namespace dot and nested-type plus: ObjectName(2) names TFM.Nucleus.Bindery.BinderyDocument
        /// tbl_Bindery_BinderyDocument. More levels than the name has gives the full name.
        /// </summary>
        public static TableNamingPolicy ObjectName(int levels = 1)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(levels, 1);
            return new(TableNamingStyle.ObjectName, levels);
        }

        /// <summary>The policy a <see cref="TableNamingAttribute"/> declares.</summary>
        public static TableNamingPolicy From(TableNamingAttribute attribute)
        {
            ArgumentNullException.ThrowIfNull(attribute);
            return attribute.Style switch
            {
                TableNamingStyle.FullName => FullName,
                TableNamingStyle.ObjectName => ObjectName(attribute.Levels),
                _ => throw new ArgumentOutOfRangeException(nameof(attribute), attribute.Style, "Unknown table naming style."),
            };
        }

        public TableNamingStyle Style { get; }

        /// <summary>The trailing parts of the full name an <see cref="TableNamingStyle.ObjectName"/> policy keeps; 0 for <see cref="FullName"/>.</summary>
        public int Levels { get; }

        /// <summary>The table name this policy gives <paramref name="type"/>.</summary>
        public string NameFor(Type type)
        {
            ArgumentNullException.ThrowIfNull(type);
            var fullName = type.FullName ?? throw new ArgumentException($"Type '{type}' has no full name.", nameof(type));
            if (Style == TableNamingStyle.FullName)
                return Prefix + fullName.Replace('.', '_').Replace('+', '_');
            var parts = fullName.Split(Separators);
            var keep = Math.Min(Levels, parts.Length);
            return Prefix + string.Join("_", parts, parts.Length - keep, keep);
        }

        public bool Equals(TableNamingPolicy? other) =>
            other is not null && Style == other.Style && Levels == other.Levels;

        public override bool Equals(object? obj) => Equals(obj as TableNamingPolicy);

        public override int GetHashCode() => HashCode.Combine(Style, Levels);

        public override string ToString() => Style == TableNamingStyle.FullName ? nameof(FullName) : $"{nameof(ObjectName)}({Levels})";
    }
}
