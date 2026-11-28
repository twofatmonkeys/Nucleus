using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus.Data
{

    /// <summary>What a predicate selects once its constant sub-conditions are folded away.</summary>
    public enum PredicateShape
    {
        /// <summary>No filter: every row.</summary>
        MatchesAll,
        /// <summary>A predicate that is false for every row, such as <c>x =&gt; false</c>.</summary>
        MatchesNone,
        /// <summary>A real filter the provider must translate.</summary>
        Filtered,
    }

    public class ExpressionParser
    {
        private readonly IQuery _query;

        public ExpressionParser(IQuery baseQuery) { _query = baseQuery; }

        /// <summary>
        /// Classifies a query expression without translating it, so a caller can skip the round
        /// trip for a predicate that matches nothing and refuse a delete that matches everything.
        /// </summary>
        public static PredicateShape Classify(Expression expression)
        {
            ArgumentNullException.ThrowIfNull(expression);
            return Normalize(expression) switch
            {
                null => PredicateShape.MatchesAll,
                ConstantExpression { Value: true } => PredicateShape.MatchesAll,
                ConstantExpression { Value: false } => PredicateShape.MatchesNone,
                _ => PredicateShape.Filtered,
            };
        }

        /// <summary>
        /// The guard every delete by predicate passes first. Returns false when the predicate
        /// matches no rows, so there is nothing to run, and throws when it matches every row:
        /// a delete of a whole table is never what a predicate meant.
        /// </summary>
        public static bool CheckDeletePredicate(Expression predicate)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            return Classify(predicate) switch
            {
                PredicateShape.MatchesNone => false,
                PredicateShape.MatchesAll => throw new ArgumentException(
                    "The predicate matches every row. Deleting a whole table by predicate is refused.", nameof(predicate)),
                _ => true,
            };
        }

        /// <summary>
        /// Translates a query expression onto <paramref name="baseQuery"/>. The expression is a
        /// predicate body, a queryable's expression (a chain of Where calls over the source, every
        /// one of them applied), or a constant meaning every row. Every term must translate: a
        /// term that cannot throws rather than being dropped, because a dropped term widens the
        /// selection, and the same translation drives deletes. A predicate that matches nothing
        /// throws too, since no WHERE can say it; callers check <see cref="Classify"/> first.
        /// </summary>
        public static IQuery ParseExpressionToQuery(IQuery baseQuery, Expression expression)
        {
            try
            {
                ArgumentNullException.ThrowIfNull(expression);
                var predicate = Normalize(expression);
                switch (predicate)
                {
                    case null:
                    case ConstantExpression { Value: true }:
                        return baseQuery;
                    case ConstantExpression { Value: false }:
                        throw new InvalidOperationException(
                            "The predicate matches no rows. Check ExpressionParser.Classify before translating it.");
                }
                var parser = new ExpressionParser(baseQuery);
                parser.Parse(predicate);
                return parser._query;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to parse expression to query", ex);
            }
        }

        /// <summary>
        /// Reduces a query expression to one folded predicate body, or null for no filter.
        /// Closures are evaluated to constants first, every Where in a chain is combined with AND,
        /// and boolean constants are folded so the result is either a constant or free of them.
        /// </summary>
        private static Expression? Normalize(Expression expression)
        {
            // A predicate handed over whole (x => x.Name == "a") means its body. Callers used to
            // have to pass .Body themselves, and a lambda compiled only to fail here.
            if (StripQuotes(expression) is LambdaExpression lambda)
                expression = lambda.Body;

            // Fold closure/local-variable sub-trees into constants before translation.
            expression = Evaluator.PartialEval(expression) ?? expression;

            Expression? predicate = null;
            var node = expression;
            // Query operators return a queryable; a bool-typed call such as Contains is a predicate.
            while (node is MethodCallExpression call && typeof(IQueryable).IsAssignableFrom(call.Type))
            {
                if (call.Method.Name != "Where" || call.Arguments.Count != 2)
                    throw new NotSupportedException(
                        $"Query operator {call.Method.Name} is not supported here: a filter, and a delete, take Where only. " +
                        "A load orders and pages with OrderBy, ThenBy, Skip and Take on the query.");
                var body = ((LambdaExpression)StripQuotes(call.Arguments[1])).Body;
                predicate = predicate == null ? body : Expression.AndAlso(body, predicate);
                node = call.Arguments[0];
            }

            if (!ReferenceEquals(node, expression))
            {
                // A Where chain ends at its source, which carries no filter of its own.
                if (node is not ConstantExpression { Value: IQueryable })
                    throw new NotSupportedException($"Query source {node.NodeType} is not supported.");
            }
            else if (expression.Type == typeof(bool))
                predicate = expression;
            else if (expression is ConstantExpression { Value: IQueryable })
                predicate = null;
            else
                throw new NotSupportedException($"Expression of type {expression.Type} is not a predicate.");

            return predicate == null ? null : FoldBooleanConstants(predicate);
        }

        /// <summary>
        /// Takes a load's query expression apart into its filter and the operators that order and
        /// cut the rows: <c>OrderBy</c>, <c>OrderByDescending</c>, <c>ThenBy</c>,
        /// <c>ThenByDescending</c>, <c>Skip</c> and <c>Take</c>. A plain filter comes back as it
        /// was given, so it translates exactly as before. Every operator is kept or refused, never
        /// dropped: a later <c>OrderBy</c> sorts first and the earlier keys still break its ties,
        /// as LINQ's stable sort does; Skip and Take compose as LINQ's do; and a Where or an order
        /// after a Skip or Take, which would act on the page rather than the rows, throws, as does
        /// any other operator. Only a load splits: a delete parses its expression whole, so an
        /// order or a limit on a delete is refused rather than deleting every match.
        /// </summary>
        internal static Queries.QueryShape Split(Expression expression)
        {
            ArgumentNullException.ThrowIfNull(expression);
            if (!HasOperators(expression))
                return Queries.QueryShape.Plain(expression);

            var calls = new List<MethodCallExpression>();
            var node = expression;
            while (node is MethodCallExpression call && typeof(IQueryable).IsAssignableFrom(call.Type))
            {
                calls.Add(call);
                node = call.Arguments[0];
            }
            if (node is not ConstantExpression { Value: IQueryable })
                throw new NotSupportedException($"Query source {node.NodeType} is not supported.");

            Expression? predicate = null;
            List<(string Property, bool Descending)>? order = null;
            var skip = 0;
            int? take = null;
            // The innermost operator applies first.
            for (var i = calls.Count - 1; i >= 0; i--)
            {
                var call = calls[i];
                if (call.Method.DeclaringType != typeof(Queryable) || call.Arguments.Count != 2)
                    throw UnsupportedOperator(call);
                switch (call.Method.Name)
                {
                    case "Where" when OperatorLambda(call).Parameters.Count == 1:
                        if (skip > 0 || take != null)
                            throw new NotSupportedException(
                                "A Where after Skip or Take would filter the page rather than the rows, which does not translate. Put the Where first.");
                        var body = OperatorLambda(call).Body;
                        predicate = predicate == null ? body : Expression.AndAlso(predicate, body);
                        break;
                    case "OrderBy" or "OrderByDescending":
                    {
                        EnsureBeforeCut(call, skip, take);
                        var key = (Property: OrderKey(call), Descending: call.Method.Name == "OrderByDescending");
                        // The new order sorts first; the earlier keys still decide its ties, as LINQ's
                        // stable sort leaves them. A repeated key adds nothing after its first use.
                        var reordered = new List<(string Property, bool Descending)> { key };
                        if (order != null)
                            foreach (var earlier in order)
                                if (earlier.Property != key.Property)
                                    reordered.Add(earlier);
                        order = reordered;
                        break;
                    }
                    case "ThenBy" or "ThenByDescending":
                    {
                        EnsureBeforeCut(call, skip, take);
                        var key = (Property: OrderKey(call), Descending: call.Method.Name == "ThenByDescending");
                        order ??= [];
                        if (!order.Exists(k => k.Property == key.Property))
                            order.Add(key);
                        break;
                    }
                    case "Skip" when call.Arguments[1].Type == typeof(int):
                    {
                        var n = Math.Max(0, RowCount(call));
                        skip = checked(skip + n);
                        if (take is { } limit)
                            take = Math.Max(0, limit - n);
                        break;
                    }
                    case "Take" when call.Arguments[1].Type == typeof(int):
                    {
                        var n = Math.Max(0, RowCount(call));
                        take = take is { } limit ? Math.Min(limit, n) : n;
                        break;
                    }
                    default:
                        throw UnsupportedOperator(call);
                }
            }
            return Queries.QueryShape.WithOperators(predicate ?? True, order, skip, take);
        }

        /// <summary>
        /// True when a query expression has an operator other than a plain Where (one argument,
        /// the row), which the split then translates or refuses. Allocates nothing.
        /// </summary>
        private static bool HasOperators(Expression expression)
        {
            var node = expression;
            while (node is MethodCallExpression call && typeof(IQueryable).IsAssignableFrom(call.Type))
            {
                if (call.Method.Name != "Where" || call.Arguments.Count != 2
                    || StripQuotes(call.Arguments[1]) is not LambdaExpression { Parameters.Count: 1 })
                    return true;
                node = call.Arguments[0];
            }
            return false;
        }

        private static LambdaExpression OperatorLambda(MethodCallExpression call) =>
            StripQuotes(call.Arguments[1]) as LambdaExpression ?? throw UnsupportedOperator(call);

        private static void EnsureBeforeCut(MethodCallExpression call, int skip, int? take)
        {
            if (skip > 0 || take != null)
                throw new NotSupportedException(
                    $"{call.Method.Name} after Skip or Take would order the page rather than the rows, which does not translate. Order first.");
        }

        /// <summary>The property an order key names: <c>x =&gt; x.Name</c>, boxed or not.</summary>
        private static string OrderKey(MethodCallExpression call)
        {
            var lambda = OperatorLambda(call);
            var body = lambda.Body;
            while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
                body = convert.Operand;
            if (lambda.Parameters.Count == 1 && body is MemberExpression { Expression: ParameterExpression parameter } member
                && parameter == lambda.Parameters[0])
                return member.Member.Name;
            throw new NotSupportedException(
                $"{call.Method.Name}({lambda}) does not translate: order by a stored property of the entity, as in x => x.Name.");
        }

        /// <summary>The number a Skip or Take is given, worked out now when it is a variable.</summary>
        private static int RowCount(MethodCallExpression call)
        {
            var argument = Evaluator.PartialEval(call.Arguments[1]) ?? call.Arguments[1];
            return argument is ConstantExpression { Value: int n }
                ? n
                : throw new NotSupportedException($"{call.Method.Name} needs a number that does not depend on the row.");
        }

        private static NotSupportedException UnsupportedOperator(MethodCallExpression call) =>
            new($"Query operator {call.Method.Name} is not supported. A load translates Where, OrderBy, OrderByDescending, " +
                "ThenBy, ThenByDescending, Skip and Take; project and group in memory, or use a row read.");

        private static readonly ConstantExpression True = Expression.Constant(true);
        private static readonly ConstantExpression False = Expression.Constant(false);

        private static bool IsConstant(Expression expression, bool value) =>
            expression is ConstantExpression { Value: bool b } && b == value;

        /// <summary>
        /// Simplifies AND, OR and NOT over boolean constants (x &amp;&amp; true is x, x || true is
        /// true, and so on), so no constant survives below the root. A constant left inside a
        /// condition has no column to translate to, and dropping it would change the meaning.
        /// </summary>
        private static Expression FoldBooleanConstants(Expression expression)
        {
            switch (expression)
            {
                case BinaryExpression { NodeType: ExpressionType.AndAlso, Method: null } and:
                {
                    var left = FoldBooleanConstants(and.Left);
                    var right = FoldBooleanConstants(and.Right);
                    if (IsConstant(left, false) || IsConstant(right, false))
                        return False;
                    if (IsConstant(left, true))
                        return right;
                    if (IsConstant(right, true))
                        return left;
                    return and.Update(left, and.Conversion, right);
                }
                case BinaryExpression { NodeType: ExpressionType.OrElse, Method: null } or:
                {
                    var left = FoldBooleanConstants(or.Left);
                    var right = FoldBooleanConstants(or.Right);
                    if (IsConstant(left, true) || IsConstant(right, true))
                        return True;
                    if (IsConstant(left, false))
                        return right;
                    if (IsConstant(right, false))
                        return left;
                    return or.Update(left, or.Conversion, right);
                }
                case UnaryExpression { NodeType: ExpressionType.Not, Method: null } not when not.Type == typeof(bool):
                {
                    var operand = FoldBooleanConstants(not.Operand);
                    if (operand is ConstantExpression { Value: bool b })
                        return b ? False : True;
                    return not.Update(operand);
                }
                // A list with nothing in it holds no value, so the term is false whatever the
                // column: the caller is told the predicate matches nothing (or, negated, every
                // row) and no query runs to find that out.
                case MethodCallExpression { Method.Name: "Contains" } call when IsEmptyListContains(call):
                    return False;
                default:
                    return expression;
            }
        }
        private static Expression StripQuotes(Expression e)
        {
            while (e.NodeType == ExpressionType.Quote)
            {
                e = ((UnaryExpression)e).Operand;
            }
            return e;
        }

        private void Parse(Expression expr)
        {
            var rootWhereCollection = _query.NewWhereCollection();
            VisitLogical(expr, rootWhereCollection);
            _query.AddWhere(rootWhereCollection);
        }

        private void VisitLogical(Expression expression, IWhereCollection whereCollection)
        {
            switch (expression.NodeType)
            {
                case ExpressionType.AndAlso:
                    VisitAndAlso((BinaryExpression)expression, whereCollection);
                    break;
                case ExpressionType.OrElse:
                    VisitOrElse((BinaryExpression)expression, whereCollection);
                    break;
                case ExpressionType.Not:
                    VisitNot((UnaryExpression)expression, whereCollection);
                    break;
                default:
                    // Handle individual conditions (comparisons, method calls, etc.)
                    VisitCondition(expression, whereCollection);
                    break;
            }
        }

        private void VisitAndAlso(BinaryExpression expression, IWhereCollection whereCollection)
        {
            // For AND operations, we can add both sides to the same collection
            VisitLogical(expression.Left, whereCollection);
            VisitLogical(expression.Right, whereCollection);
        }

        /// <summary>
        /// An OR always becomes its own group, ANDed into the enclosing collection, with one
        /// branch collection per operand (a chain of ORs shares the group). Adding the operands
        /// straight to the enclosing collection let the provider regroup them, so
        /// A &amp;&amp; (B || C) rendered as (A AND B) OR C.
        /// </summary>
        private void VisitOrElse(BinaryExpression expression, IWhereCollection whereCollection)
        {
            var group = _query.NewWhereCollection();
            AddOrBranches(expression, group);
            whereCollection.AddWhereAnd(group);
        }

        private void AddOrBranches(Expression expression, IWhereCollection group)
        {
            if (expression is BinaryExpression { NodeType: ExpressionType.OrElse } or)
            {
                AddOrBranches(or.Left, group);
                AddOrBranches(or.Right, group);
                return;
            }
            var branch = _query.NewWhereCollection();
            VisitLogical(expression, branch);
            group.AddWhereOr(branch);
        }

        private void VisitNot(UnaryExpression expression, IWhereCollection whereCollection)
        {
            // !x.Flag translates to Flag = false.
            if (IsBooleanColumn(expression.Operand, out var member))
            {
                whereCollection.AddAnd(new Parameter(GetMemberName(member), false, Operator.Equals));
                return;
            }
            var parameter = ExtractParameter(expression.Operand)
                ?? throw new NotSupportedException($"NOT operation on {expression.Operand.NodeType} is not supported");
            parameter.Operator = NegateOperator(parameter.Operator);
            whereCollection.AddAnd(parameter);
        }

        /// <summary>A bare bool column used as a condition, as in x =&gt; x.IsArchived.</summary>
        private static bool IsBooleanColumn(Expression expression, out MemberExpression member)
        {
            if (StripConversions(expression) is MemberExpression m
                && m.Expression?.NodeType == ExpressionType.Parameter
                && m.Type == typeof(bool))
            {
                member = m;
                return true;
            }
            member = null!;
            return false;
        }

        private static Operator NegateOperator(Operator op) => op switch
        {
            Operator.Equals => Operator.NotEquals,
            Operator.NotEquals => Operator.Equals,
            Operator.GreaterThan => Operator.LessThanOrEqualTo,
            Operator.LessThan => Operator.GreaterThanOrEqualTo,
            Operator.GreaterThanOrEqualTo => Operator.LessThan,
            Operator.LessThanOrEqualTo => Operator.GreaterThan,
            _ => throw new NotSupportedException($"Operator {op} cannot be negated"),
        };

        private void VisitCondition(Expression expression, IWhereCollection whereCollection)
        {
            var parameter = ExtractParameter(expression)
                ?? throw new NotSupportedException(
                    $"The condition {expression} does not translate to a column term. Dropping it would widen the selection.");
            whereCollection.AddAnd(parameter);
        }

        private Parameter? ExtractParameter(Expression expression)
        {
            switch (expression.NodeType)
            {
                case ExpressionType.Equal:
                    return HandleComparison((BinaryExpression)expression, Operator.Equals);
                case ExpressionType.NotEqual:
                    return HandleComparison((BinaryExpression)expression, Operator.NotEquals);
                case ExpressionType.GreaterThan:
                    return HandleComparison((BinaryExpression)expression, Operator.GreaterThan);
                case ExpressionType.GreaterThanOrEqual:
                    return HandleComparison((BinaryExpression)expression, Operator.GreaterThanOrEqualTo);
                case ExpressionType.LessThan:
                    return HandleComparison((BinaryExpression)expression, Operator.LessThan);
                case ExpressionType.LessThanOrEqual:
                    return HandleComparison((BinaryExpression)expression, Operator.LessThanOrEqualTo);
                case ExpressionType.Call:
                    return HandleMethodCall((MethodCallExpression)expression);
                case ExpressionType.MemberAccess:
                    return HandleMemberAccess((MemberExpression)expression);
                case ExpressionType.Constant:
                    // Constants by themselves don't create parameters
                    return null;
                default:
                    throw new NotSupportedException($"Expression type {expression.NodeType} is not supported");
            }
        }

        private Parameter HandleComparison(BinaryExpression expression, Operator op)
        {
            var (column, valueExpression, columnOnRight) = ColumnAndValue(expression.Left, expression.Right);
            var memberName = GetMemberName(column);
            var value = GetExpressionValue(valueExpression);

            // 3 < x.Quantity is x.Quantity > 3: the term is always written column first.
            return new Parameter(memberName, value, columnOnRight ? Mirror(op) : op);
        }

        /// <summary>The operator that keeps a comparison's meaning when its two sides swap.</summary>
        private static Operator Mirror(Operator op) => op switch
        {
            Operator.GreaterThan => Operator.LessThan,
            Operator.LessThan => Operator.GreaterThan,
            Operator.GreaterThanOrEqualTo => Operator.LessThanOrEqualTo,
            Operator.LessThanOrEqualTo => Operator.GreaterThanOrEqualTo,
            _ => op,
        };

        private Parameter? HandleMethodCall(MethodCallExpression expression)
        {
            switch (expression.Method.Name)
            {
                case "Contains":
                    return HandleContains(expression);
                case "StartsWith":
                    return HandleStartsWith(expression);
                case "EndsWith":
                    return HandleEndsWith(expression);
                case "Equals":
                    return HandleEquals(expression, false);
                default:
                    throw new NotSupportedException($"Method {expression.Method.Name} is not supported");
            }
        }

        private Parameter? HandleContains(MethodCallExpression expression)
        {
            if (expression.Object != null && expression.Method.DeclaringType == typeof(string))
            {
                // String.Contains is a full-text search on a FullTextSearch column and a substring
                // match on any other, so every provider means the same by it.
                var memberName = GetMemberName(expression.Object);
                var value = GetExpressionValue(expression.Arguments[0]);
                return new Parameter(memberName, value, IsFullTextSearch(expression.Object) ? Operator.Contains : Operator.Like);
            }
            else if (expression.Object != null)
            {
                // Instance collection.Contains(member) -> IN clause
                var memberName = GetMemberName(expression.Arguments[0]);
                var listValue = GetExpressionValue(expression.Object);
                return BuildCollectionParameter(memberName, listValue);
            }
            else
            {
                // Static/extension Enumerable.Contains(collection, member) -> IN clause.
                // From C# 13 an array receiver binds to MemoryExtensions.Contains instead, whose
                // first argument is an implicit conversion to ReadOnlySpan<T>; unwrap it so both
                // spellings translate to the same IN clause.
                var listValue = GetExpressionValue(UnwrapSpanConversion(expression.Arguments[0]));
                var memberName = GetMemberName(expression.Arguments[1]);
                return BuildCollectionParameter(memberName, listValue);
            }
        }

        /// <summary>
        /// Returns the operand behind an implicit Span/ReadOnlySpan conversion, or the expression
        /// itself when it is not one. A ref struct cannot be evaluated into a constant, but the
        /// array or list it was converted from can.
        /// </summary>
        private static Expression UnwrapSpanConversion(Expression expression)
        {
            if (expression is UnaryExpression { NodeType: ExpressionType.Convert } convert
                && convert.Type.IsByRefLike)
                return UnwrapSpanConversion(convert.Operand);

            if (expression is MethodCallExpression { Method.Name: "op_Implicit" } call
                && call.Type.IsByRefLike
                && call.Arguments.Count == 1)
                return UnwrapSpanConversion(call.Arguments[0]);

            return expression;
        }

        /// <summary>
        /// A collection's Contains over a list already evaluated to a constant that holds no
        /// value: nothing at all, or only nulls, which no comparison matches. The list is found as
        /// <see cref="HandleContains"/> finds it.
        /// </summary>
        private static bool IsEmptyListContains(MethodCallExpression call)
        {
            Expression? list;
            if (call.Object != null)
                list = call.Method.DeclaringType == typeof(string) ? null : call.Object;
            else
                list = call.Arguments.Count == 2 ? UnwrapSpanConversion(call.Arguments[0]) : null;
            // A query is a list too, but reading it here would run it.
            if (list is not ConstantExpression { Value: System.Collections.IEnumerable values and not string and not IQueryable })
                return false;
            foreach (var value in values)
                if (value != null)
                    return false;
            return true;
        }

        private static Parameter? BuildCollectionParameter(string memberName, object? listValue)
        {
            if (listValue is System.Collections.IEnumerable && listValue is not string)
                return new Parameter(memberName, listValue, Operator.In);
            return new Parameter(memberName, listValue, Operator.Equals);
        }

        private Parameter HandleStartsWith(MethodCallExpression expression)
        {
            ArgumentNullException.ThrowIfNull(expression.Object, nameof(expression.Object));
            var memberName = GetMemberName(expression.Object);
            var value = GetExpressionValue(expression.Arguments[0]);
            return new Parameter(memberName, value, Operator.StartsWith);
        }

        private Parameter HandleEndsWith(MethodCallExpression expression)
        {
            ArgumentNullException.ThrowIfNull(expression.Object, nameof(expression.Object));
            var memberName = GetMemberName(expression.Object);
            var value = GetExpressionValue(expression.Arguments[0]);
            return new Parameter(memberName, value, Operator.EndsWith);
        }

        private Parameter HandleEquals(MethodCallExpression expression, bool negate = false)
        {
            if (expression.Object != null)
            {
                // Instance method call: obj.Equals(value)
                var memberName = GetMemberName(expression.Object);
                var value = GetExpressionValue(expression.Arguments[0]);
                var op = negate ? Operator.NotEquals : Operator.Equals;
                return new Parameter(memberName, value, op);
            }
            else
            {
                // Static method call: string.Equals(left, right, StringComparison)
                if (expression.Arguments.Count >= 2)
                {
                    bool isCaseInsensitive = false;

                    // Check if there's a StringComparison parameter for case-insensitive comparison
                    if (expression.Arguments.Count >= 3 &&
                        expression.Method.DeclaringType == typeof(string))
                    {
                        var comparisonArg = expression.Arguments[2];
                        if (comparisonArg is ConstantExpression constExpr &&
                            constExpr.Value is StringComparison comparison)
                        {
                            isCaseInsensitive = comparison == StringComparison.CurrentCultureIgnoreCase ||
                                              comparison == StringComparison.InvariantCultureIgnoreCase ||
                                              comparison == StringComparison.OrdinalIgnoreCase;
                        }
                    }

                    var (column, valueExpression, _) = ColumnAndValue(expression.Arguments[0], expression.Arguments[1]);
                    var memberName = GetMemberName(column);
                    var value = GetExpressionValue(valueExpression);

                    // Determine operator based on case sensitivity and negation
                    Operator op;
                    if (negate)
                    {
                        op = Operator.NotEquals; // Note: might need special handling for case-insensitive NOT equals
                    }
                    else
                    {
                        op = isCaseInsensitive ? Operator.EqualsIgnoreCase : Operator.Equals;
                    }

                    return new Parameter(memberName, value, op);
                }
                else
                {
                    throw new NotSupportedException("Equals method call with less than 2 arguments is not supported");
                }
            }
        }

        private Parameter? HandleMemberAccess(MemberExpression expression)
        {
            // A bare bool column is a condition in its own right: x => x.IsArchived.
            if (IsBooleanColumn(expression, out var member))
                return new Parameter(GetMemberName(member), true, Operator.Equals);

            if (IsRootedAtParameter(expression))
            {
                // Throws the reference message for x.Parent.IsActive; anything else is not a condition.
                ColumnMember(expression);
                throw new NotSupportedException($"The condition {expression} is not a bool column of the queried type.");
            }

            // This is accessing a variable or field from outside the expression
            var value = GetExpressionValue(expression);
            var memberName = GetMemberName(expression);
            return new Parameter(memberName, value, Operator.Equals);
        }

        /// <summary>
        /// Returns the operand behind any chain of Convert nodes, or the expression itself when it
        /// is not one. C# lowers an enum comparison to Convert(x.Prop, Int32) == Constant(int), and
        /// a nullable enum to a Nullable&lt;Int32&gt; conversion on both sides, so the column is an
        /// inner node rather than the operand itself. Ref-struct conversions are left alone: those
        /// are span conversions and belong to <see cref="UnwrapSpanConversion"/>, which is the only
        /// place that knows they cannot be folded into a constant.
        /// </summary>
        private static Expression StripConversions(Expression expression)
        {
            while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert
                && !convert.Type.IsByRefLike)
                expression = convert.Operand;
            return expression;
        }

        /// <summary>
        /// Splits a comparison into its column and its value, on whichever side the column is
        /// written. A comparison with no column, or with a column on both sides, does not
        /// translate.
        /// </summary>
        private static (MemberExpression Column, Expression Value, bool ColumnOnRight) ColumnAndValue(Expression left, Expression right)
        {
            var leftColumn = ColumnMember(left);
            var rightColumn = ColumnMember(right);
            if (leftColumn != null && rightColumn != null)
                throw new NotSupportedException($"{left} and {right} are both columns. A comparison between two columns does not translate.");
            if (leftColumn != null)
                return (leftColumn, right, false);
            if (rightColumn != null)
                return (rightColumn, left, true);
            throw new NotSupportedException("At least one side of comparison must be a member access");
        }

        /// <summary>
        /// The member that names a column, or null when the expression is not one. A column is a
        /// member of the parameter (x.Name), or a referenced entity read through its ID
        /// (x.Parent.ID, x.Customer.ID): an entity reference is stored as its ID, so its column is
        /// the reference itself. Any other read through the parameter throws, because it names
        /// no column of the queried table and dropping or guessing it would change the selection.
        /// </summary>
        private static MemberExpression? ColumnMember(Expression expression)
        {
            if (StripConversions(expression) is not MemberExpression { Expression: { } owner } member)
                return null;
            owner = StripConversions(owner);
            if (owner is ParameterExpression)
                return member;
            if (owner is MemberExpression { Expression: { } referenceOwner } reference
                && StripConversions(referenceOwner) is ParameterExpression
                && typeof(ICoreObject).IsAssignableFrom(reference.Type))
            {
                if (member.Member.Name == nameof(ICoreObject.ID))
                    return reference;
                throw new NotSupportedException(
                    $"{member} reads {member.Member.Name} of the referenced entity {reference.Member.Name}. Only the ID of a referenced entity translates " +
                    $"(x.{reference.Member.Name}.ID, or x.{reference.Member.Name} == entity); a column of the referenced entity needs a join, which the parser does not do.");
            }
            if (IsRootedAtParameter(owner))
                throw new NotSupportedException(
                    $"{member} is not a column of the queried type. Only its own columns and the ID of a referenced entity translate.");
            return null;
        }

        /// <summary>A member chain that starts at the predicate's parameter, such as x.Parent.Name.</summary>
        private static bool IsRootedAtParameter(Expression expression)
        {
            while (true)
            {
                switch (StripConversions(expression))
                {
                    case ParameterExpression:
                        return true;
                    case MemberExpression { Expression: { } owner }:
                        expression = owner;
                        continue;
                    default:
                        return false;
                }
            }
        }

        /// <summary>
        /// The column a member maps to. The query's table knows it, inherited properties
        /// included; a query without a table falls back to the member's own attribute. An entity
        /// reference read through its ID maps to the reference's column.
        /// </summary>
        private string GetMemberName(Expression expression)
        {
            if ((ColumnMember(expression) ?? StripConversions(expression) as MemberExpression) is { } member)
            {
                if (_query.DataTableSchema?.FindByPropertyName(member.Member.Name) is { } column)
                    return column.ColumnName;
                if (_query.DataTableSchema != null && typeof(ICoreObject).IsAssignableFrom(member.Type))
                    throw new NotSupportedException(
                        $"{member.Member.Name} references an entity but has no column in {_query.DataTableSchema.TableName}. Mark it [DatabaseColumn] to filter on it.");
                var attr = member.Member.GetCustomAttribute<DatabaseColumnAttribute>(inherit: true);
                return string.IsNullOrWhiteSpace(attr?.ColumnName) ? member.Member.Name : attr.ColumnName;
            }
            throw new NotSupportedException($"Cannot extract member name from expression type {expression.GetType()}");
        }

        /// <summary>The member's column carries FullTextSearch, found as <see cref="GetMemberName"/> finds the column.</summary>
        private bool IsFullTextSearch(Expression expression)
        {
            if (StripConversions(expression) is not MemberExpression member)
                return false;
            IDatabaseColumnAttribute? attr = _query.DataTableSchema?.FindByPropertyName(member.Member.Name)?.PropertySchema.DatabaseColumnAttribute
                ?? member.Member.GetCustomAttribute<DatabaseColumnAttribute>(inherit: true);
            return attr?.FullTextSearch == true;
        }

        private object? GetExpressionValue(Expression expression)
        {
            if (expression is ConstantExpression constant)
                return NormalizeValue(constant.Value);

            // Compile and execute the expression to get the value
            var lambda = Expression.Lambda(expression);
            return NormalizeValue(lambda.Compile().DynamicInvoke());
        }

        /// <summary>
        /// Binds a value as the type its column actually stores: an enum as its underlying
        /// integral value and an entity as its ID, and an enumerable of either as an enumerable of
        /// those, rather than relying on the driver to recognise them. The write path does the
        /// same in SqlDataContext.CoerceValueForDataTable and ColumnSchema.GetClrValue; this is
        /// the read path.
        /// </summary>
        private static object? NormalizeValue(object? value)
        {
            switch (value)
            {
                case null:
                    return null;
                case ICoreObject entity:
                    return entity.ID;
            }

            var type = value.GetType();
            if (type.IsEnum)
                return Convert.ChangeType(value, Enum.GetUnderlyingType(type));

            // Decided from the declared element type, never by enumerating: mapping every
            // enumerable would turn a byte[] term into an object[] and break binary comparisons.
            if (value is System.Collections.IEnumerable items and not string && ElementType(type) is Type elementType)
            {
                if (elementType.IsEnum)
                {
                    var underlying = Enum.GetUnderlyingType(elementType);
                    var mapped = new List<object?>();
                    foreach (var item in items)
                        mapped.Add(item == null ? null : Convert.ChangeType(item, underlying));
                    return mapped;
                }
                if (typeof(ICoreObject).IsAssignableFrom(elementType))
                {
                    var ids = new List<object?>();
                    foreach (var item in items)
                        ids.Add(item is ICoreObject entity ? entity.ID : null);
                    return ids;
                }
            }

            return value;
        }

        /// <summary>
        /// The element type behind an IEnumerable&lt;T&gt; that needs mapping: an enum (or a
        /// nullable enum, unwrapped) or an entity. Null when the sequence is of anything else.
        /// </summary>
        private static Type? ElementType(Type sequenceType)
        {
            foreach (var candidate in sequenceType.GetInterfaces().Append(sequenceType))
            {
                if (!candidate.IsGenericType || candidate.GetGenericTypeDefinition() != typeof(IEnumerable<>))
                    continue;
                var element = candidate.GetGenericArguments()[0];
                element = Nullable.GetUnderlyingType(element) ?? element;
                if (element.IsEnum || typeof(ICoreObject).IsAssignableFrom(element))
                    return element;
            }
            return null;
        }
    }
}
