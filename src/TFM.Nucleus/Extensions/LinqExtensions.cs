using System.Linq.Expressions;
using System.Reflection;

namespace TFM.Nucleus
{
    public static class LinqExtensions
    {
        public static object GetValue(this MethodCallExpression expression) => Evaluate(expression);

        public static object GetValue(this MemberExpression member) => Evaluate(member);

        /// <summary>
        /// Reads the value an expression node evaluates to. Constants and closure field reads,
        /// which are the overwhelmingly common cases in a translated predicate, are read directly;
        /// only anything else pays for compiling a lambda.
        /// </summary>
        private static object Evaluate(Expression expression)
        {
            if (TryReadDirectly(expression, out var value))
                return value!;

            var getter = Expression.Lambda<Func<object>>(Expression.Convert(expression, typeof(object))).Compile();
            return getter();
        }

        private static bool TryReadDirectly(Expression expression, out object? value)
        {
            switch (expression)
            {
                case ConstantExpression constant:
                    value = constant.Value;
                    return true;

                // A captured local becomes a field on a compiler generated closure object, which
                // reaches us as a member access over a constant. Closures can nest, so recurse.
                case MemberExpression member:
                    object? instance = null;
                    if (member.Expression is not null && !TryReadDirectly(member.Expression, out instance))
                        break;
                    switch (member.Member)
                    {
                        case FieldInfo field:
                            value = field.GetValue(instance);
                            return true;
                        case PropertyInfo property when property.GetIndexParameters().Length == 0:
                            value = property.GetValue(instance);
                            return true;
                    }
                    break;
            }

            value = null;
            return false;
        }
    }
}
