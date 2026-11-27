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
            // Filled in place and trimmed only when values were dropped. A short list, which most
            // are, finds its repeats by looking; a set is built once a list outgrows that.
            var size = values is ICollection collection ? collection.Count : 4;
            var kept = size == 0 ? [] : new object[size];
            var count = 0;
            HashSet<object>? seen = null;
            Type? type = null;
            var mixed = false;
            foreach (var value in values)
            {
                if (value is null)
                    continue;
                if (seen != null)
                {
                    if (!seen.Add(value))
                        continue;
                }
                else if (Array.IndexOf(kept, value, 0, count) >= 0)
                    continue;
                else if (count == LookLimit)
                {
                    seen = new HashSet<object>(Math.Max(size, LookLimit * 2));
                    for (var i = 0; i < count; i++)
                        seen.Add(kept[i]);
                    seen.Add(value);
                }
                if (count == kept.Length)
                    Array.Resize(ref kept, Math.Max(4, count * 2));
                kept[count++] = value;
                var valueType = value.GetType();
                if (type == null)
                    type = valueType;
                else if (type != valueType)
                    mixed = true;
            }
            if (count != kept.Length)
                Array.Resize(ref kept, count);
            return new ValueList(kept, mixed ? null : type);
        }

        /// <summary>Up to this many values a repeat is found by looking through those kept.</summary>
        private const int LookLimit = 16;

        public IEnumerator<object> GetEnumerator() => ((IEnumerable<object>)_values).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => _values.GetEnumerator();

        public override string ToString() => $"{Count} values";
    }
}
