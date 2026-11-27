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

        /// <summary>
        /// The name the value is bound under. A context numbers the terms of each command it
        /// runs (<see cref="NameForCommand"/>), so one shape of command is one text however many
        /// have run. A term nobody numbered names itself on first use, uniquely in the process.
        /// </summary>
        public string? Variable => _variable ??= _numbered ? NextVariableName() : null;
        private string? _variable;

        /// <summary>True for a term named by number; false for one bound under its column's name, which stays.</summary>
        private readonly bool _numbered;

        /// <summary>The command this term was last numbered for, so a term two statements share is numbered once.</summary>
        private int _command;

        /// <summary>
        /// Names the term <c>p</c><paramref name="number"/> for the command <paramref name="command"/>
        /// identifies, and returns true, unless it keeps a name of its own or the command has
        /// named it already. Called before the command's text is built and its values bound,
        /// which both read <see cref="Variable"/>.
        /// </summary>
        internal bool NameForCommand(int command, int number)
        {
            if (!_numbered || _command == command)
                return false;
            _command = command;
            _variable = NumberedName(number);
            return true;
        }

        // The names of the first terms of a command are shared strings: a term then costs no
        // string of its own, where each used to allocate one when it was built.
        private static readonly string?[] Names = new string?[4096];

        private static string NumberedName(int number) =>
            (uint)number < (uint)Names.Length ? Names[number] ??= $"p{number}" : $"p{number}";

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
            _numbered = true;
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
            _numbered = random;
            _variable = random ? null : name;
            Operator = oprtr;
        }

        public ParameterCollection CreateCollection()
        {
            return [this];
        }

        // For a term no command numbered: a process-wide counter, unique in any command. A
        // command's terms are numbered together or not at all, so the two kinds never meet.
        private static int _variableCounter;
        private static string NextVariableName() => $"p{(uint)Interlocked.Increment(ref _variableCounter)}";

        public override string ToString() => $"{Name} {Operator} {Value ?? "null"}";
    }
}
