using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.Data.Queries
{
    /// <summary>
    /// How a term's value becomes command parameters. A multi-valued (IN) term binds either as one
    /// parameter holding every value, when the renderer says it writes the term that way
    /// (<see cref="IQueryRenderer.BindsAsList"/>), or as one parameter per value, named by
    /// <see cref="ElementName"/>. A renderer references the same names and reads the same
    /// <see cref="ValueList"/>, so the text and the bound parameters cannot drift apart.
    /// </summary>
    public static class ParameterBinding
    {
        /// <summary>True for an IN term over a collection. A string is scalar, never expanded.</summary>
        public static bool IsMultiValued(IQueryParameter p) =>
            p.Operator == Operator.In && p.Value is IEnumerable and not string;

        /// <summary>The distinct, non-null values of a multi-valued (IN) term.</summary>
        public static IReadOnlyList<object?> MultiValues(IQueryParameter p) => Values(p);

        /// <summary>The values of a multi-valued (IN) term, as the list a <see cref="Parameter"/> keeps.</summary>
        public static ValueList Values(IQueryParameter p) =>
            p.Value is IEnumerable enumerable and not string
                ? ValueList.From(enumerable)
                : throw new NotSupportedException($"Operator.In requires an enumerable value; got {p.Value?.GetType().ToString() ?? "null"}.");

        /// <summary>The variable name of element <paramref name="index"/> of a multi-valued term.</summary>
        public static string ElementName(IQueryParameter p, int index) => $"{p.Variable}_{index}";

        /// <summary>
        /// The (variable name, value) pairs a term binds, without the provider's prefix. A
        /// multi-valued term binds each value under its own name, or, with
        /// <paramref name="asList"/>, its <see cref="ValueList"/> under the term's variable. A
        /// pattern term binds its value escaped by <see cref="LikePattern"/>.
        /// </summary>
        public static IEnumerable<KeyValuePair<string, object?>> Bind(IQueryParameter p, bool asList = false)
        {
            if (IsMultiValued(p))
            {
                var values = Values(p);
                if (asList)
                {
                    yield return new(p.Variable!, values);
                    yield break;
                }
                for (int i = 0; i < values.Count; i++)
                    yield return new(ElementName(p, i), values[i]);
            }
            else
            {
                yield return new(p.Variable!, LikePattern.Applies(p.Operator) && p.Value is string pattern ? LikePattern.Escape(pattern) : p.Value);
            }
        }

        /// <summary>Every pair bound by the terms that use a variable, each value of a list under its own name.</summary>
        public static IEnumerable<KeyValuePair<string, object?>> Bind(IEnumerable<IQueryParameter?>? terms) =>
            terms == null ? [] : terms.Where(p => p != null && p.UsesVariable).SelectMany(p => Bind(p!));

        private static int _commands;

        /// <summary>
        /// Numbers the terms of one command, in the order given: <c>p0</c>, <c>p1</c> and on. The
        /// context calls this before it builds the command's text, so two commands of one shape
        /// have one text, which an engine that caches plans by text can then reuse. Named by a
        /// counter for the whole process, as they were, no two commands ever had the same text,
        /// and every one was compiled afresh.
        /// </summary>
        public static void NameForCommand(IEnumerable<IQueryParameter?>? terms)
        {
            if (terms == null)
                return;
            // Zero is a term no command has numbered.
            var command = System.Threading.Interlocked.Increment(ref _commands);
            if (command == 0)
                command = System.Threading.Interlocked.Increment(ref _commands);
            var number = 0;
            foreach (var term in terms)
                if (term is Parameter parameter && parameter.NameForCommand(command, number))
                    number++;
        }

        /// <summary>How many command parameters <paramref name="terms"/> bind as <paramref name="renderer"/> writes them.</summary>
        public static int Count(IEnumerable<IQueryParameter?>? terms, IQueryRenderer renderer)
        {
            ArgumentNullException.ThrowIfNull(renderer);
            if (terms == null)
                return 0;
            var count = 0;
            foreach (var term in terms)
            {
                if (term == null || !term.UsesVariable)
                    continue;
                count += IsMultiValued(term) && !renderer.BindsAsList(term) ? Values(term).Count : 1;
            }
            return count;
        }
    }
}
