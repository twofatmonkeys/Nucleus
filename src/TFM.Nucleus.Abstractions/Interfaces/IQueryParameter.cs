using System;

namespace TFM.Nucleus.Data
{
    /// <summary>
    /// A provider-neutral query term. Rendering into SQL (or any other dialect) is exclusively
    /// the data provider's responsibility.
    /// </summary>
    public interface IQueryParameter
    {
        public bool UsesVariable { get; }
        public bool NeedsOperator { get; }
        public string? Name { get; }
        public object? Value { get; }
        public string? Variable { get; }
        public Operator Operator { get; }

        public IQueryParameterCollection CreateCollection();

        [Obsolete("Use AsOutputParameter.")]
        public IQueryOutputParameter castAsOutputParameter();

        /// <summary>This parameter as an output parameter; throws <see cref="InvalidCastException"/> when it is not one.</summary>
        public IQueryOutputParameter AsOutputParameter() =>
#pragma warning disable CS0618 // an implementation that predates this member answers under the old name
            castAsOutputParameter();
#pragma warning restore CS0618
    }
}
