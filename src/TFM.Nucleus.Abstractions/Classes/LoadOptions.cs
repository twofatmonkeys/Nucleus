using System.Collections.Generic;

namespace TFM.Nucleus.Data
{
    /// <summary>
    /// What a load brings in beside the rows themselves. The two lists are named, so they cannot
    /// be passed in each other's place, which the overloads taking two collections of names allowed.
    /// </summary>
    public sealed record LoadOptions
    {
        /// <summary>
        /// Child collection properties to leave unloaded, by name (<c>nameof(Order.Lines)</c>):
        /// their rows are neither queried nor transferred, and reading one throws until it is loaded.
        /// </summary>
        public IReadOnlyCollection<string> Exclude { get; init; } = [];

        /// <summary>
        /// What to load with the rows in the same round trip, by property name: a lazy
        /// (<c>[RetrieveOnDemand]</c>) child collection, a linked collection, or the entity of a
        /// <c>[LinkedObject]</c> property.
        /// </summary>
        public IReadOnlyCollection<string> Include { get; init; } = [];

        /// <summary>
        /// Also return the rows of a soft-deletable type that are marked deleted, and those of
        /// its child collections. Without it a load sees live rows only.
        /// </summary>
        public bool IncludeDeleted { get; init; }

        /// <summary>A load with nothing excluded, nothing included and live rows only.</summary>
        public static LoadOptions Default { get; } = new();
    }
}
