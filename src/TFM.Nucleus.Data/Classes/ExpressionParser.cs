using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
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
            // Fold closure/local-variable sub-trees into constants before translation.
            expression = Evaluator.PartialEval(expression) ?? expression;

            Expression? predicate = null;
            var node = expression;
            // Query operators return a queryable; a bool-typed call such as Contains is a predicate.
            while (node is MethodCallExpression call && typeof(IQueryable).IsAssignableFrom(call.Type))
            {
                if (call.Method.Name != "Where" || call.Arguments.Count != 2)
                    throw new NotSupportedException(
                        $"Query operator {call.Method.Name} is not supported. Only Where translates; order, page and project in memory.");
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
            var memberExpression = GetMemberExpression(expression.Left, expression.Right);

            if (memberExpression == null)
                throw new NotSupportedException("At least one side of comparison must be a member access");

            var valueExpression = ValueSideOf(expression.Left, expression.Right, memberExpression);
            var memberName = GetMemberName(memberExpression);
            var value = GetExpressionValue(valueExpression);

            return new Parameter(memberName, value, op);
        }

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
                // String.Contains -> substring match
                var memberName = GetMemberName(expression.Object);
                var value = GetExpressionValue(expression.Arguments[0]);
                return new Parameter(memberName, value, Operator.Contains);
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

                    var memberExpression = GetMemberExpression(expression.Arguments[0], expression.Arguments[1]);

                    if (memberExpression == null)
                        throw new NotSupportedException("At least one side of comparison must be a member access");

                    var valueExpression = ValueSideOf(expression.Arguments[0], expression.Arguments[1], memberExpression);
                    var memberName = GetMemberName(memberExpression);
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
            if (expression.Expression?.NodeType != ExpressionType.Parameter)
            {
                // This is accessing a variable or field from outside the expression
                var value = GetExpressionValue(expression);
                var memberName = GetMemberName(expression);
                return new Parameter(memberName, value, Operator.Equals);
            }

            // A bare bool column is a condition in its own right: x => x.IsArchived.
            if (IsBooleanColumn(expression, out var member))
                return new Parameter(GetMemberName(member), true, Operator.Equals);

            return null;
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

        private MemberExpression? GetMemberExpression(Expression left, Expression right)
        {
            if (StripConversions(left) is MemberExpression leftMember && leftMember.Expression?.NodeType == ExpressionType.Parameter)
                return leftMember;
            if (StripConversions(right) is MemberExpression rightMember && rightMember.Expression?.NodeType == ExpressionType.Parameter)
                return rightMember;
            return null;
        }

        /// <summary>
        /// The side of a comparison that is not the column. The member may be nested inside a
        /// Convert, so the sides are matched after stripping rather than by reference to the
        /// original nodes.
        /// </summary>
        private static Expression ValueSideOf(Expression left, Expression right, MemberExpression member) =>
            ReferenceEquals(StripConversions(left), member) ? right : left;

        private string GetMemberName(Expression expression)
        {
            if (StripConversions(expression) is MemberExpression member)
            {
                if (Attribute.IsDefined(member.Member, typeof(DatabaseColumnAttribute)))
                {
                    var attr = (DatabaseColumnAttribute[])member.Member.GetCustomAttributes(typeof(DatabaseColumnAttribute), false);
                    return attr.Length > 0 ? attr[0].ColumnName : member.Member.Name;
                }
                return member.Member.Name;
            }
            throw new NotSupportedException($"Cannot extract member name from expression type {expression.GetType()}");
        }

        private object? GetExpressionValue(Expression expression)
        {
            if (expression is ConstantExpression constant)
                return NormalizeEnums(constant.Value);

            // Compile and execute the expression to get the value
            var lambda = Expression.Lambda(expression);
            return NormalizeEnums(lambda.Compile().DynamicInvoke());
        }

        /// <summary>
        /// Binds an enum as its underlying integral value, and an enumerable of enums as an
        /// enumerable of those values, so a term reaches the provider as the type its column
        /// actually stores rather than relying on the driver to recognise the enum. The write path
        /// does the same in SqlDataContext.CoerceValueForDataTable; this is the read path.
        /// </summary>
        private static object? NormalizeEnums(object? value)
        {
            if (value == null)
                return null;

            var type = value.GetType();
            if (type.IsEnum)
                return Convert.ChangeType(value, Enum.GetUnderlyingType(type));

            // Decided from the declared element type, never by enumerating: mapping every
            // enumerable would turn a byte[] term into an object[] and break binary comparisons.
            if (value is System.Collections.IEnumerable items and not string
                && EnumElementType(type) is Type elementType)
            {
                var underlying = Enum.GetUnderlyingType(elementType);
                var mapped = new List<object?>();
                foreach (var item in items)
                    mapped.Add(item == null ? null : Convert.ChangeType(item, underlying));
                return mapped;
            }

            return value;
        }

        /// <summary>
        /// The enum type behind an IEnumerable&lt;TEnum&gt; or IEnumerable&lt;TEnum?&gt;, or null
        /// when the sequence is of anything else.
        /// </summary>
        private static Type? EnumElementType(Type sequenceType)
        {
            foreach (var candidate in sequenceType.GetInterfaces().Append(sequenceType))
            {
                if (!candidate.IsGenericType || candidate.GetGenericTypeDefinition() != typeof(IEnumerable<>))
                    continue;
                var element = candidate.GetGenericArguments()[0];
                element = Nullable.GetUnderlyingType(element) ?? element;
                if (element.IsEnum)
                    return element;
            }
            return null;
        }
    }
}
