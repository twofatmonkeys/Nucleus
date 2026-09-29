using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using TFM.Nucleus.Abstractions.Interfaces;

namespace TFM.Nucleus
{
    /// <summary>
    /// Names the child collections a load leaves out, from selectors such as <c>b =&gt; b.Files</c>.
    /// Exclusions are kept by property name, since two collections may hold the same child type.
    /// </summary>
    public static class ChildCollectionSelector
    {
        /// <summary>The name of the collection property a selector reads.</summary>
        public static string PropertyName<T>(Expression<Func<T, INucleusChildCollection>> selector)
        {
            ArgumentNullException.ThrowIfNull(selector);
            var body = selector.Body;
            while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
                body = convert.Operand;
            if (body is MemberExpression { Expression: ParameterExpression } member)
                return member.Member.Name;
            throw new ArgumentException(
                $"A child collection selector must read one collection property of {typeof(T).Name}, as in x => x.Items; got {selector}.",
                nameof(selector));
        }

        /// <summary>The distinct property names of several selectors.</summary>
        public static IReadOnlyCollection<string> PropertyNames<T>(IEnumerable<Expression<Func<T, INucleusChildCollection>>> selectors)
        {
            ArgumentNullException.ThrowIfNull(selectors);
            return selectors.Select(PropertyName).Distinct(StringComparer.Ordinal).ToArray();
        }
    }
}
