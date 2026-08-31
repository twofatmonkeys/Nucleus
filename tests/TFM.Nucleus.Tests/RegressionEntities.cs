using Microsoft.Extensions.Logging;

namespace TFM.Nucleus.Tests
{
    /// <summary>Exercises nullable, enum and non-public-setter columns.</summary>
    public class NullableWidget : CoreObject<NullableWidget>
    {
        [DatabaseColumn]
        public int? OptionalQuantity
        {
            get => GetValue<int?>();
            set => SetValue(value);
        }

        [DatabaseColumn]
        public decimal? OptionalPrice
        {
            get => GetValue<decimal?>();
            set => SetValue(value);
        }

        [DatabaseColumn]
        public WidgetKind Kind
        {
            get => GetValue<WidgetKind>();
            set => SetValue(value);
        }

        [DatabaseColumn]
        public WidgetKind? OptionalKind
        {
            get => GetValue<WidgetKind?>();
            set => SetValue(value);
        }

        /// <summary>A protected setter: writes through the schema must still reach it.</summary>
        [DatabaseColumn]
        public string? RestrictedName
        {
            get => GetValue<string>();
            protected set => SetValue(value);
        }
    }

    public enum WidgetKind
    {
        Unknown = 0,
        Standard = 1,
        Premium = 2,
    }

    /// <summary>A base type whose member a derived type shadows with "new".</summary>
    public class ShadowBase : CoreObject<ShadowBase>
    {
        [DatabaseColumn]
        public string? Label
        {
            get => GetValue<string>();
            set => SetValue(value);
        }
    }

    public class ShadowDerived : ShadowBase
    {
        [DatabaseColumn]
        public new string? Label
        {
            get => GetValue<string>();
            set => SetValue(value);
        }
    }

    /// <summary>Target type for ChangeTypeOfT rewriting.</summary>
    public class SourceShape
    {
        public string? Name { get; set; }
        public int Quantity { get; set; }
        public bool Active { get; set; }
    }

    public class TargetShape
    {
        public string? Name { get; set; }
        public int Quantity { get; set; }
        public bool Active { get; set; }
    }
}
