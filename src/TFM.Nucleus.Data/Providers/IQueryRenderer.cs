using TFM.Nucleus.Data.Queries;

namespace TFM.Nucleus.Data.Providers
{
    /// <summary>
    /// Turns the neutral query model into one provider's dialect. A provider implements this once
    /// and attaches it to every query it builds; the query classes decide what a query means and
    /// the renderer only decides how it is written.
    /// </summary>
    public interface IQueryRenderer
    {
        /// <summary>
        /// The select batch: the rows, then the unpaged count when <see cref="SelectQuery.WithTotalCount"/>
        /// is set, then, when <paramref name="includeChildQueries"/> is set, each child collection
        /// query in <see cref="Query.LinkedQueries"/> pre-order, which is the order a reader meets
        /// their result sets.
        /// </summary>
        string Select(SelectQuery query, bool includeChildQueries);

        /// <summary>The insert, followed by its linked queries.</summary>
        string Insert(InsertQuery query);

        /// <summary>The update, followed by its linked queries.</summary>
        string Update(UpdateQuery query);

        /// <summary>
        /// One chunk of a delete: every target of the cascade, children first, then a statement
        /// whose scalar result is the number of root rows affected. Linked chunks are separate
        /// commands and are never appended.
        /// </summary>
        string DeleteWhere(DeleteWhereQuery query);
    }

    /// <summary>
    /// The dialect pieces <see cref="WhereRenderer"/> needs to write a condition tree: one term,
    /// the two joiners and a parenthesised group.
    /// </summary>
    public interface IConditionWriter
    {
        /// <summary>One comparison term.</summary>
        string Term(Parameter term);

        /// <summary>The text between two operands of an AND group, spaces included.</summary>
        string And { get; }

        /// <summary>The text between two operands of an OR group, spaces included.</summary>
        string Or { get; }

        /// <summary>A condition wrapped so it binds as one operand.</summary>
        string Group(string condition);
    }
}
