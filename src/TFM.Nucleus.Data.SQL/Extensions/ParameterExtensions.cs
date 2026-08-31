using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace TFM.Nucleus.Data.SQL
{
    /// <summary>
    /// The single home of SQL Server rendering for query terms. The neutral Parameter model
    /// carries no SQL; everything dialect-specific lives here.
    /// </summary>
    public static class ParameterExtensions
    {
        private const string CaseSensitiveCollation = "Latin1_General_CS_AS";

        public static string LeftOperand(this Parameter p) => $"[{p.Name}]";

        /// <summary>The @variable reference for a bound term.</summary>
        public static string VariableReference(this Parameter p) => $"@{p.Variable}";

        /// <summary>
        /// The values of a multi-valued (IN) term. Strings are scalar, never expanded.
        /// </summary>
        public static IReadOnlyList<object?> MultiValues(this Parameter p) =>
            p.Value is IEnumerable enumerable and not string
                ? [.. enumerable.Cast<object?>()]
                : throw new NotSupportedException($"Operator.In requires an enumerable value; got {p.Value?.GetType().ToString() ?? "null"}.");

        public static bool IsMultiValued(this Parameter p) =>
            p.Operator == Operator.In && p.Value is IEnumerable and not string;

        private static string SQLOperatorString(this Parameter p)
        {
            if (!p.NeedsOperator)
                return "";
            switch (p.Operator)
            {
                case Operator.Equals:
                case Operator.EqualsIgnoreCase:
                    return "=";
                case Operator.Like:
                case Operator.LikeCaseSensitive:
                case Operator.StartsWith:
                case Operator.EndsWith:
                    return "LIKE";
                case Operator.GreaterThan:
                    return ">";
                case Operator.LessThan:
                    return "<";
                case Operator.GreaterThanOrEqualTo:
                    return ">=";
                case Operator.LessThanOrEqualTo:
                    return "<=";
                case Operator.NotEquals:
                    return "<>";
                default:
                    throw new NotSupportedException($"Unsupported operator ({p.Operator}) found when generating SQL Operator String");
            }
        }

        public static string SQLWhereCondition(this Parameter p)
        {
            switch (p.Operator)
            {
                case Operator.Contains:
                    return $"CONTAINS({p.LeftOperand()}, {p.VariableReference()})";
                case Operator.ContainsCaseSensitive:
                    return $"{p.LeftOperand()} COLLATE {CaseSensitiveCollation} LIKE '%' + {p.VariableReference()} + '%'";
                case Operator.Like:
                    return $"{p.LeftOperand()} {p.SQLOperatorString()} '%' + {p.VariableReference()} + '%'";
                case Operator.LikeCaseSensitive:
                    return $"{p.LeftOperand()} COLLATE {CaseSensitiveCollation} {p.SQLOperatorString()} '%' + {p.VariableReference()} + '%'";
                case Operator.StartsWith:
                    return $"{p.LeftOperand()} {p.SQLOperatorString()} {p.VariableReference()} + '%'";
                case Operator.EndsWith:
                    return $"{p.LeftOperand()} {p.SQLOperatorString()} '%' + {p.VariableReference()}";
                case Operator.EqualsIgnoreCase:
                    return $"LOWER({p.LeftOperand()}) {p.SQLOperatorString()} LOWER({p.VariableReference()})";
                case Operator.In:
                    return p.SqlInCondition();
                default:
                    break;
            }
            if (!p.UsesVariable && p.Value == null)
            {
                if (p.Operator == Operator.Equals)
                    return $"{p.LeftOperand()} IS NULL";
                else if (p.Operator == Operator.NotEquals)
                    return $"{p.LeftOperand()} IS NOT NULL";
                else
                    throw new NotSupportedException($"Operator {p.Operator} with a null value is not supported in SQLWhereCondition.");
            }
            return $"{p.LeftOperand()}{p.SQLOperatorString()}{p.RightOperandExpression()}";
        }

        /// <summary>
        /// IN over the term's values, one command parameter per element ({variable}_{index}).
        /// An empty list renders as a no-match predicate.
        /// </summary>
        private static string SqlInCondition(this Parameter p)
        {
            var count = p.MultiValues().Count;
            if (count == 0)
                return "1 = 0";
            var references = Enumerable.Range(0, count).Select(i => $"@{p.Variable}_{i}");
            return $"{p.LeftOperand()} IN ({string.Join(",", references)})";
        }

        private static string RightOperandExpression(this Parameter p)
        {
            if (p.UsesVariable)
                return p.VariableReference();
            if (p.Value is IServerFunction function)
                return function.RenderServerFunction();
            throw new NotSupportedException($"Unsupported operator ({p.Operator}) with Null Value for use with a Database Parameter");
        }

        public static string SQLSetCondition(this Parameter p) =>
            $"{p.LeftOperand()} = {p.SQLSetValue()}";

        public static string SQLSetValue(this Parameter p)
        {
            if (p.UsesVariable)
                return p.VariableReference();
            else if (p.Value == null)
                return "NULL";
            else if (p.Value is IServerFunction function)
                return function.RenderServerFunction();
            else
                throw new Exception("Unknown Value Type to set.");
        }

        /// <summary>Maps a neutral server-function token to its SQL Server dialect.</summary>
        public static string RenderServerFunction(this IServerFunction function) =>
            function.Kind switch
            {
                ServerFunctionKind.SysDateTime => "SYSDATETIME()",
                ServerFunctionKind.SysDateTimeOffset => "SYSDATETIMEOFFSET()",
                ServerFunctionKind.UtcNow => "GETUTCDATE()",
                ServerFunctionKind.NewGuid => "NEWID()",
                _ => throw new NotSupportedException($"Server function {function.Kind} is not supported by the SQL Server provider."),
            };
    }
}
