using System.Linq.Expressions;
using System.Reflection;


namespace TFM.Nucleus
{
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class ExpressionExtensions
    {
        public static Expression<Func<T, bool>> AndAlso<T>(
        this Expression<Func<T, bool>> expr1,
        Expression<Func<T, bool>> expr2)
        {
            // need to detect whether they use the same
            // parameter instance; if not, they need fixing
            ParameterExpression param = expr1.Parameters[0];
            if (ReferenceEquals(param, expr2.Parameters[0]))
            {
                // simple version
                return Expression.Lambda<Func<T, bool>>(
                    Expression.AndAlso(expr1.Body, expr2.Body), param);
            }
            // otherwise, keep expr1 "as is" and invoke expr2
            return Expression.Lambda<Func<T, bool>>(
                Expression.AndAlso(
                    expr1.Body,
                    Expression.Invoke(expr2, param)), param);
        }

        /// <summary>
        /// Rewrites a predicate over <typeparamref name="TInput"/> as the same predicate over
        /// <typeparamref name="TOutput"/>, rebinding members and methods declared on the input type
        /// to the identically named members of the output type.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <typeparamref name="TOutput"/> has no matching member for one used by the predicate.
        /// </exception>
        public static Expression<Func<TOutput, bool>> ChangeTypeOfT<TInput, TOutput>(this Expression<Func<TInput, bool>> expression)
        {
            ArgumentNullException.ThrowIfNull(expression);

            var parameter = Expression.Parameter(typeof(TOutput), expression.Parameters[0].Name);
            var rewriter = new TypeRebinder(typeof(TInput), typeof(TOutput), expression.Parameters[0], parameter);
            var body = rewriter.Visit(expression.Body);
            return Expression.Lambda<Func<TOutput, bool>>(body, parameter);
        }

        /// <summary>
        /// Replaces the source parameter and rebinds anything declared on the source type onto the
        /// target type. Implemented as an ExpressionVisitor so that every node type is handled:
        /// the previous hand written switch covered six of them and returned the rest untouched,
        /// which left them referencing the old parameter and failing at compile time with
        /// "variable of type X referenced from scope".
        /// </summary>
        private sealed class TypeRebinder(Type sourceType, Type targetType, ParameterExpression source, ParameterExpression target)
            : ExpressionVisitor
        {
            protected override Expression VisitParameter(ParameterExpression node) =>
                node == source || node.Type == sourceType ? target : node;

            protected override Expression VisitMember(MemberExpression node)
            {
                var instance = Visit(node.Expression);
                if (!IsSourceMember(node.Member))
                    return node.Update(instance);

                var member = targetType.GetMember(node.Member.Name,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy).FirstOrDefault()
                    ?? throw new ArgumentException(
                        $"Type {targetType.Name} has no member named '{node.Member.Name}' to rebind from {sourceType.Name}.");

                return Expression.MakeMemberAccess(instance, member);
            }

            protected override Expression VisitMethodCall(MethodCallExpression node)
            {
                var instance = Visit(node.Object);
                var arguments = node.Arguments.Select(Visit).ToArray()!;

                if (node.Method.DeclaringType is null || !IsSourceType(node.Method.DeclaringType))
                    return node.Update(instance, arguments!);

                // An instance method declared on the source type: find its counterpart.
                var parameterTypes = node.Method.GetParameters().Select(p => p.ParameterType).ToArray();
                var method = targetType.GetMethod(node.Method.Name,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy,
                    binder: null, parameterTypes, modifiers: null)
                    ?? throw new ArgumentException(
                        $"Type {targetType.Name} has no method '{node.Method.Name}' to rebind from {sourceType.Name}.");

                return Expression.Call(instance, method, arguments!);
            }

            private bool IsSourceMember(MemberInfo member) =>
                member.DeclaringType is not null && IsSourceType(member.DeclaringType);

            private bool IsSourceType(Type type) =>
                type == sourceType || type.IsAssignableFrom(sourceType);
        }
    }
}
