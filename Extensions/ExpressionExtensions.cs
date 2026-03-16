using System.Linq.Expressions;


namespace TFM.Nucleus
{
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

        public static Expression<Func<TOutput, bool>> ChangeTypeOfT<TInput, TOutput>(this Expression<Func<TInput, bool>> expression)
        {

            var parms = new List<ParameterExpression>();
            foreach (var p in expression.Parameters)
            {
                if (p.Type == typeof(TInput))
                {
                    var newParm = Expression.Parameter(typeof(TOutput), p.Name);
                    parms.Add(newParm);
                }
                else
                    parms.Add(p);
            }
            return Expression.Lambda<Func<TOutput, bool>>(expression.Body.ConvertMembers<TInput, TOutput>(parms), parms);
        }
        private static Expression ConvertMembers<TInput, TOutput>(this Expression expression, IReadOnlyCollection<ParameterExpression> parms)
        {
            if (expression.NodeType == ExpressionType.OrElse ||
                expression.NodeType == ExpressionType.AndAlso ||
                expression.NodeType == ExpressionType.Equal ||
                expression.NodeType == ExpressionType.NotEqual)
            {
                var exp = (BinaryExpression)expression;
                var left = exp.Left.ConvertMembers<TInput, TOutput>(parms);
                var right = exp.Right.ConvertMembers<TInput, TOutput>(parms);
                return Expression.MakeBinary(exp.NodeType, left, right);
            }
            else if (expression.NodeType == ExpressionType.Call)
            {

                var exp = (MethodCallExpression)expression;
                //Convert Any Member arguments of type
                var hasNewArgs = false;
                var newArgs = new List<Expression>();
                foreach (var a in exp.Arguments)
                {
                    if (a.NodeType == ExpressionType.MemberAccess)
                    {
                        var arg = (MemberExpression)a;
                        if (arg.Member.DeclaringType == typeof(TInput))
                        {
                            newArgs.Add(a.ConvertMembers<TInput, TOutput>(parms));
                            hasNewArgs = true;
                        }
                        else
                            newArgs.Add(a);
                    }
                    else
                        newArgs.Add(a);
                }
                //recreate expression with new args
                if (hasNewArgs)
                {
                    expression = Expression.Call(null, exp.Method, newArgs);
                }
                //return if static
                if (exp.Object == null)
                    return expression;
                //Convert member type if not static
                var member = ((MemberExpression)exp.Object).Member;
                if (member.DeclaringType == typeof(TInput))
                {
                    //var parm = Expression.Parameter(typeof(TOutput), parms.Single(x => x.Type == typeof(TOutput)).Name);
                    var obj = Expression.Property(parms.Single(x => x.Type == typeof(TOutput)), typeof(TOutput), member.Name);
                    //var mem = Expression.MakeMemberAccess(obj, typeof(TOutput).GetProperty(member.Name));
                    var m = Expression.Call(obj, exp.Method, exp.Arguments);
                    return m;
                }
                return expression;
            }
            else if (expression.NodeType == ExpressionType.MemberAccess)
            {
                var me = ((MemberExpression)expression);
                if (me.Member.DeclaringType == typeof(TInput))
                {
                    //var parm = Expression.Parameter(typeof(TOutput), parms.Single(x => x.Type == typeof(TOutput)).Name);
                    var memAcc = Expression.Property(parms.Single(x => x.Type == typeof(TOutput)), typeof(TOutput), me.Member.Name);
                    return memAcc;
                }
                return expression;
            }
            else
            {
                return expression;
            }

        }

    }
}
