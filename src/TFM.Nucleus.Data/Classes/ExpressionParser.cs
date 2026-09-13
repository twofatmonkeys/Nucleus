using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus.Data
{

    public class ExpressionParser
    {
        private readonly IQuery _query;

        public ExpressionParser(IQuery baseQuery) { _query = baseQuery; }

        public static IQuery ParseExpressionToQuery(IQuery baseQuery, Expression expression)
        {
            try
            {
                var parser = new ExpressionParser(baseQuery);
                if(expression == null)
                    throw new ArgumentNullException(nameof(expression));
                // Fold closure/local-variable sub-trees into constants before translation.
                expression = Evaluator.PartialEval(expression) ?? expression;
                if(expression.NodeType == ExpressionType.Call)
                {
                    var methodCall = (MethodCallExpression)expression;
                    if (methodCall.Method.Name == "Where" && methodCall.Arguments.Count == 2)
                    {
                        var lambda = (LambdaExpression)StripQuotes(methodCall.Arguments[1]);
                        parser.Parse(lambda.Body);
                        return parser._query;
                    }
                }


                parser.Parse(expression);
                return parser._query;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to parse expression to query", ex);
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

        private void VisitOrElse(BinaryExpression expression, IWhereCollection whereCollection)
        {
            // For OR operations, we need to handle each side separately
            // and add them as OR conditions to the collection

            if (IsSimpleCondition(expression.Left) && IsSimpleCondition(expression.Right))
            {
                // Both sides are simple conditions - add them directly as OR
                var leftParam = ExtractParameter(expression.Left);
                var rightParam = ExtractParameter(expression.Right);

                if (leftParam != null && rightParam != null)
                {
                    whereCollection.AddAnd(leftParam);
                    whereCollection.AddOr(rightParam);
                }
            }
            else
            {
                // At least one side is complex - create nested where collections
                var leftCollection = _query.NewWhereCollection();
                var rightCollection = _query.NewWhereCollection();

                VisitLogical(expression.Left, leftCollection);
                VisitLogical(expression.Right, rightCollection);

                whereCollection.AddWhereAnd(leftCollection);
                whereCollection.AddWhereOr(rightCollection);
            }
        }

        private void VisitNot(UnaryExpression expression, IWhereCollection whereCollection)
        {
            var parameter = ExtractParameter(expression.Operand)
                ?? throw new NotSupportedException($"NOT operation on {expression.Operand.NodeType} is not supported");
            parameter.Operator = NegateOperator(parameter.Operator);
            whereCollection.AddAnd(parameter);
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
            var parameter = ExtractParameter(expression);
            if (parameter != null)
            {
                whereCollection.AddAnd(parameter);
            }
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

            // If it's a parameter member access (x.Property), it should be handled in comparisons
            return null;
        }

        private bool IsSimpleCondition(Expression expression)
        {
            // A simple condition is one that directly creates a single parameter
            // (comparisons, method calls, but not logical operations)
            return expression.NodeType switch
            {
                ExpressionType.Equal => true,
                ExpressionType.NotEqual => true,
                ExpressionType.GreaterThan => true,
                ExpressionType.GreaterThanOrEqual => true,
                ExpressionType.LessThan => true,
                ExpressionType.LessThanOrEqual => true,
                ExpressionType.Call => true,
                ExpressionType.MemberAccess => true,
                ExpressionType.Constant => true,
                ExpressionType.AndAlso => false,
                ExpressionType.OrElse => false,
                ExpressionType.Not => false,
                _ => false
            };
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
