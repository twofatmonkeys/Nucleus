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
            var valueExpression = memberExpression == expression.Left ? expression.Right : expression.Left;

            if (memberExpression == null)
                throw new NotSupportedException("At least one side of comparison must be a member access");

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
                // Static/extension Enumerable.Contains(collection, member) -> IN clause
                var listValue = GetExpressionValue(expression.Arguments[0]);
                var memberName = GetMemberName(expression.Arguments[1]);
                return BuildCollectionParameter(memberName, listValue);
            }
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
                    var valueExpression = memberExpression == expression.Arguments[0] ? expression.Arguments[1] : expression.Arguments[0];

                    if (memberExpression == null)
                        throw new NotSupportedException("At least one side of comparison must be a member access");

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

        private MemberExpression? GetMemberExpression(Expression left, Expression right)
        {
            if (left is MemberExpression leftMember && leftMember.Expression?.NodeType == ExpressionType.Parameter)
                return leftMember;
            if (right is MemberExpression rightMember && rightMember.Expression?.NodeType == ExpressionType.Parameter)
                return rightMember;
            return null;
        }

        private string GetMemberName(Expression expression)
        {
            if (expression is MemberExpression member)
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
            {
                return constant.Value;
            }

            // Compile and execute the expression to get the value
            var lambda = Expression.Lambda(expression);
            return lambda.Compile().DynamicInvoke();
        }
    }
}
