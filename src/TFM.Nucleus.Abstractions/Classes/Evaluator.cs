using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;

namespace TFM.Nucleus
{
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public static class Evaluator
    {
        /// <summary> 
        /// Performs evaluation and replacement of independent sub-trees 
        /// </summary> 
        /// <param name="expression">The root of the expression tree.</param>
        /// <param name="fnCanBeEvaluated">A function that decides whether a given expression node can be part of the local function.</param>
        /// <returns>A new tree with sub-trees evaluated and replaced.</returns> 
        public static Expression? PartialEval(Expression expression, Func<Expression, bool> fnCanBeEvaluated)
        {
            return new SubtreeEvaluator(new Nominator(fnCanBeEvaluated).Nominate(expression)).Eval(expression);
        }

        /// <summary> 
        /// Performs evaluation and replacement of independent sub-trees 
        /// </summary> 
        /// <param name="expression">The root of the expression tree.</param>
        /// <returns>A new tree with sub-trees evaluated and replaced.</returns> 
        public static Expression? PartialEval(Expression expression)
        {
            return PartialEval(expression, Evaluator.CanBeEvaluatedLocally);
        }

        private static bool CanBeEvaluatedLocally(Expression expression)
        {
            if (expression.NodeType == ExpressionType.Parameter)
                return false;

            // A ref struct (Span&lt;T&gt;, ReadOnlySpan&lt;T&gt;) cannot be boxed into an
            // Expression.Constant nor returned through Delegate.DynamicInvoke. From C# 13 the
            // compiler binds someArray.Contains(x) to MemoryExtensions.Contains, whose first
            // argument is an implicit span conversion, so folding it would throw
            // NotSupportedException. Leave those nodes in the tree for the parser to unwrap.
            if (expression.Type.IsByRefLike)
                return false;

            // A query operator over a query source (source.Take(5)) has no parameter, so it looked
            // like a closure value: it was run and replaced by the queryable it returned, and the
            // operator vanished before the parser saw it. A query is translated, never evaluated.
            // A queryable built from a value (ids.AsQueryable()) is still folded: its source is not a query.
            // The type is tested first and the arguments read through IArgumentProvider: reading
            // Arguments builds a collection on every call node, a predicate's Contains included.
            if (expression is MethodCallExpression call
                && typeof(System.Linq.IQueryable).IsAssignableFrom(call.Type)
                && ((IArgumentProvider)call).ArgumentCount > 0
                && typeof(System.Linq.IQueryable).IsAssignableFrom(((IArgumentProvider)call).GetArgument(0).Type))
                return false;

            return true;
        }

        /// <summary> 
        /// Evaluates and replaces sub-trees when first candidate is reached (top-down) 
        /// </summary> 
        class SubtreeEvaluator : ExpressionVisitor
        {
            HashSet<Expression> candidates;

            internal SubtreeEvaluator(HashSet<Expression> candidates)
            {
                this.candidates = candidates;
            }

            internal Expression? Eval(Expression exp)
            {
                return this.Visit(exp);
            }

            public override Expression? Visit(Expression? exp)
            {
                if (exp == null)
                {
                    return null;
                }
                if (this.candidates.Contains(exp))
                {
                    return this.Evaluate(exp);
                }
                return base.Visit(exp);
            }

            private Expression Evaluate(Expression e)
            {
                if (e.NodeType == ExpressionType.Constant)
                {
                    return e;
                }
                LambdaExpression lambda = Expression.Lambda(e);
                Delegate fn = lambda.Compile();
                return Expression.Constant(fn.DynamicInvoke(null), e.Type);
            }
        }

        /// <summary> 
        /// Performs bottom-up analysis to determine which nodes can possibly 
        /// be part of an evaluated sub-tree. 
        /// </summary> 
        class Nominator : ExpressionVisitor
        {
            Func<Expression, bool> fnCanBeEvaluated;
            HashSet<Expression>? candidates;
            bool cannotBeEvaluated;

            internal Nominator(Func<Expression, bool> fnCanBeEvaluated)
            {
                this.fnCanBeEvaluated = fnCanBeEvaluated;
            }

            internal HashSet<Expression> Nominate(Expression expression)
            {
                this.candidates = new HashSet<Expression>();
                this.Visit(expression);
                return this.candidates;
            }

            public override Expression? Visit(Expression? expression)
            {
                if (expression != null)
                {
                    bool saveCannotBeEvaluated = this.cannotBeEvaluated;
                    this.cannotBeEvaluated = false;
                    base.Visit(expression);
                    if (!this.cannotBeEvaluated)
                    {
                        if (this.fnCanBeEvaluated(expression))
                        {
                            candidates?.Add(expression);
                        }
                        else
                        {
                            this.cannotBeEvaluated = true;
                        }
                    }
                    this.cannotBeEvaluated |= saveCannotBeEvaluated;
                }
                return expression;
            }
        }
    }
}
