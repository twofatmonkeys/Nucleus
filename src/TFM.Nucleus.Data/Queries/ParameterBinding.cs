using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace TFM.Nucleus.Data.Queries
{
    /// <summary>
    /// How a term's value becomes command parameters. A multi-valued (IN) term expands to one
    /// parameter per element, named by <see cref="ElementName"/>; a renderer references the same
    /// names, so the text and the bound parameters cannot drift apart.
    /// </summary>
    public static class ParameterBinding
    {
        /// <summary>True for an IN term over a collection. A string is scalar, never expanded.</summary>
        public static bool IsMultiValued(IQueryParameter p) =>
            p.Operator == Operator.In && p.Value is IEnumerable and not string;

        /// <summary>The values of a multi-valued (IN) term.</summary>
        public static IReadOnlyList<object?> MultiValues(IQueryParameter p) =>
            p.Value is IEnumerable enumerable and not string
                ? [.. enumerable.Cast<object?>()]
                : throw new NotSupportedException($"Operator.In requires an enumerable value; got {p.Value?.GetType().ToString() ?? "null"}.");

        /// <summary>The variable name of element <paramref name="index"/> of a multi-valued term.</summary>
        public static string ElementName(IQueryParameter p, int index) => $"{p.Variable}_{index}";

        /// <summary>
        /// The (variable name, value) pairs a term binds, without the provider's prefix. A
        /// multi-valued term binds each element, with a null element bound as <see cref="DBNull"/>.
        /// </summary>
        public static IEnumerable<KeyValuePair<string, object?>> Bind(IQueryParameter p)
        {
            if (IsMultiValued(p))
            {
                var values = MultiValues(p);
                for (int i = 0; i < values.Count; i++)
                    yield return new(ElementName(p, i), values[i] ?? DBNull.Value);
            }
            else
            {
                yield return new(p.Variable!, p.Value);
            }
        }

        /// <summary>Every pair bound by the terms that use a variable.</summary>
        public static IEnumerable<KeyValuePair<string, object?>> Bind(IEnumerable<IQueryParameter?>? terms) =>
            terms == null ? [] : terms.Where(p => p != null && p.UsesVariable).SelectMany(p => Bind(p!));
    }
}
