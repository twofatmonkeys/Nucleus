using System;
using System.Linq;
using TFM.Nucleus.Data.Providers;

namespace TFM.Nucleus.Data.Queries
{
    /// <summary>
    /// The one walk of a <see cref="WhereCollection"/> tree. Every provider writes its conditions
    /// through here, so the grouping rule has a single home: an OR group inside an AND is
    /// grouped, because AND binds tighter than OR, and without it (A OR B) AND C would read as
    /// A OR B AND C. Each operand of an OR is grouped too.
    /// </summary>
    public static class WhereRenderer
    {
        public static string Render(WhereCollection where, IConditionWriter writer)
        {
            ArgumentNullException.ThrowIfNull(where);
            ArgumentNullException.ThrowIfNull(writer);
            return where.ExpressionType switch
            {
                WhereCollection.Type.Parameter => writer.Term(where.Term!),
                WhereCollection.Type.And => string.Join(writer.And, where.Terms.Select(c =>
                    c.ExpressionType == WhereCollection.Type.Or ? writer.Group(Render(c, writer)) : Render(c, writer))),
                WhereCollection.Type.Or => string.Join(writer.Or, where.Terms.Select(c => writer.Group(Render(c, writer)))),
                WhereCollection.Type.Empty => "",
                _ => throw new InvalidOperationException("Bad WhereParameter"),
            };
        }
    }
}
