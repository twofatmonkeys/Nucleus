using System;
using System.Collections;
using System.Collections.Generic;

namespace TFM.Nucleus.Data.Queries
{
    /// <summary>
    /// The values of a multi-valued term, read once from what the caller gave: each distinct value
    /// in the order it first appeared, without nulls, which no list comparison matches. A
    /// renderer and the binder read the same list, so the text and the bound values cannot differ,
    /// and a caller's list of 50,000 keys is walked once however often the term is asked.
    /// </summary>
    public sealed class ValueList : IReadOnlyList<object>
    {
        private readonly object[] _values;

        private ValueList(object[] values, Type? elementType)
        {
            _values = values;
            ElementType = elementType;
        }

        /// <summary>
        /// The one type every value has, or null for an empty list and a list of several types. A
        /// provider binds a list as one parameter only when it knows the type of its elements.
        /// </summary>
        public Type? ElementType { get; }

        public int Count => _values.Length;

        public object this[int index] => _values[index];

        /// <summary>The list of <paramref name="values"/>, which is itself when it already is one.</summary>
        public static ValueList From(IEnumerable values)
        {
            ArgumentNullException.ThrowIfNull(values);
            if (values is ValueList list)
                return list;
            var seen = new HashSet<object>();
            var kept = new List<object>(values is ICollection collection ? collection.Count : 0);
            Type? type = null;
            var mixed = false;
            foreach (var value in values)
            {
                if (value is null || !seen.Add(value))
                    continue;
                kept.Add(value);
                var valueType = value.GetType();
                if (type == null)
                    type = valueType;
                else if (type != valueType)
                    mixed = true;
            }
            return new ValueList(kept.ToArray(), mixed ? null : type);
        }

        public IEnumerator<object> GetEnumerator() => ((IEnumerable<object>)_values).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => _values.GetEnumerator();

        public override string ToString() => $"{Count} values";
    }
}
