using System;
using System.Threading;

namespace TFM.Nucleus.Data
{
    /// <summary>
    /// A provider-neutral query term: a column name, a value, an operator and the variable name
    /// used when the value is bound as a command parameter. Contains no SQL — providers render
    /// terms into their own dialect (see the SQL provider's ParameterExtensions).
    /// </summary>
    public class Parameter : IQueryParameter
    {
        #region IQueryParameter Implementation
        IQueryParameterCollection IQueryParameter.CreateCollection() =>
            CreateCollection().AsQueryParameters();
[Obsolete("Use AsOutputParameter.")]
        public IQueryOutputParameter castAsOutputParameter() => AsOutputParameter();

        /// <inheritdoc cref="IQueryParameter.AsOutputParameter"/>
        public virtual IQueryOutputParameter AsOutputParameter() => throw new InvalidCastException("Cannot cast a Parameter to an Output Parameter");
        #endregion

        public string? Name { get => _name; }
        private readonly string? _name;

        public readonly bool IsOutputParameter = false;

        public object? Value
        {
            get
            {
                if (_value != null && _value is IDatabaseStringValue stringValue)
                    return stringValue.DatabaseStringValue;
                else
                    return _value;
            }
        }
        private readonly object? _value;

#pragma warning disable CS0618 // the obsolete field is the storage, so code compiled against it still works
        public IDataColumnSchema? ColumnSchema => columnSchema;
#pragma warning restore CS0618

        /// <summary>The old name of <see cref="ColumnSchema"/>, differing from it only by case.</summary>
        [Obsolete("Use ColumnSchema.")]
        public IDataColumnSchema? columnSchema;

        public string? Variable => _variable;
        private readonly string? _variable;

        public Operator Operator { get; set; }

        /// <summary>
        /// True when the value is bound as a command parameter; false when the term has no
        /// bindable value (null comparisons) or the value is a server-evaluated function.
        /// </summary>
        public bool UsesVariable =>
            _value != null && _value is not IServerFunction;

        /// <summary>False for null equality/inequality terms, which render as IS [NOT] NULL.</summary>
        public bool NeedsOperator =>
            !(Value == null && (Operator == Operator.Equals || Operator == Operator.NotEquals));

        public Parameter()
        { }
        public Parameter(IDataColumnSchema colSchema, object? value)
        {
#pragma warning disable CS0618 // the obsolete field is the storage
            columnSchema = colSchema;
#pragma warning restore CS0618
            _name = colSchema.ColumnName;
            _value = colSchema.GetClrValue(value);
            _variable = NextVariableName();
            Operator = Operator.Equals;
        }
        protected internal Parameter(IDataColumnSchema colSchema, object? value, bool isOutputParameter) : this(colSchema, value)
        {
            IsOutputParameter = isOutputParameter;
        }
        public Parameter(string name, object? value) : this(name, value, true, Operator.Equals)
        {
        }
        protected internal Parameter(bool isOutputParameter, string name, object? value) : this(name, value, true, Operator.Equals)
        {
            IsOutputParameter = isOutputParameter;
        }
        public Parameter(string name, object? value, bool random) : this(name, value, random, Operator.Equals)
        {
        }
        public Parameter(string name, object? value, Operator oprtr) : this(name, value, true, oprtr)
        {
        }
        public Parameter(string name, object? value, bool random, Operator oprtr)
        {
            _name = name;
            // A list is read once here, into its distinct values, however often the term is
            // rendered, counted and bound.
            _value = oprtr == Operator.In && value is System.Collections.IEnumerable list and not string
                ? Queries.ValueList.From(list)
                : value;
            _variable = random ? NextVariableName() : name;
            Operator = oprtr;
        }

        public ParameterCollection CreateCollection()
        {
            return [this];
        }

        // Process-wide counter guarantees variable-name uniqueness within any batched command.
        private static int _variableCounter;
        private static string NextVariableName() => $"p{(uint)Interlocked.Increment(ref _variableCounter)}";

        public override string ToString() => $"{Name} {Operator} {Value ?? "null"}";
    }
}
