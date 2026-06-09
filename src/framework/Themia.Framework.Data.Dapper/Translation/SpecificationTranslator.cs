using System.Collections;
using System.Linq.Expressions;
using SqlKata;
using Themia.Framework.Data.Abstractions.Exceptions;
using Themia.Framework.Data.Abstractions.Specifications;
using Themia.Framework.Data.Dapper.Mapping;

namespace Themia.Framework.Data.Dapper.Translation;

// Translates an ISpecification onto a SqlKata Query. Single-table predicates only; anything outside
// the supported subset throws UnsupportedSpecificationException (drop to tier-2 provider-native SqlKata).
// Supported: ==,!=,>,>=,<,<= ; &&,||,! ; null checks -> IS NULL / IS NOT NULL ;
// string Contains/StartsWith/EndsWith -> LIKE with % ; collection Contains -> IN ;
// member access on the entity root only ; OrderBy member selectors ; Skip/Take.
// Captured variables/closures are evaluated to parameter values, never inlined.
internal static class SpecificationTranslator
{
    public static void Apply<T>(Query query, ISpecification<T> spec, EntityMapping map)
    {
        if (spec.Criteria is not null)
            query.Where(q => Translate(q, spec.Criteria.Body, spec.Criteria.Parameters[0], map));

        foreach (var order in spec.OrderBy)
        {
            var column = ColumnOf(order.KeySelector.Body, order.KeySelector.Parameters[0], map);
            if (order.Descending) query.OrderByDesc(column); else query.OrderBy(column);
        }

        if (spec.Skip is { } skip) query.Offset(skip);
        if (spec.Take is { } take) query.Limit(take);
    }

    private static Query Translate(Query q, Expression expr, ParameterExpression root, EntityMapping map)
    {
        switch (expr)
        {
            case BinaryExpression { NodeType: ExpressionType.AndAlso } b:
                return q.Where(inner => Translate(inner, b.Left, root, map)).Where(inner => Translate(inner, b.Right, root, map));
            case BinaryExpression { NodeType: ExpressionType.OrElse } b:
                return q.Where(inner => Translate(inner, b.Left, root, map)).OrWhere(inner => Translate(inner, b.Right, root, map));
            case UnaryExpression { NodeType: ExpressionType.Not } u:
                return q.WhereNot(inner => Translate(inner, u.Operand, root, map));
            case BinaryExpression b:
                return Comparison(q, b, root, map);
            case MethodCallExpression m:
                return MethodCall(q, m, root, map);
            case MemberExpression { Type.Name: nameof(Boolean) } member when IsEntityColumn(member, root):
                return q.Where(ColumnOf(member, root, map), true);
            default:
                throw new UnsupportedSpecificationException($"Unsupported predicate '{expr}'. Use provider-native (tier-2) SqlKata for this query.");
        }
    }

    private static Query Comparison(Query q, BinaryExpression b, ParameterExpression root, EntityMapping map)
    {
        var (memberSide, valueSide, op) = Orient(b, root);
        var column = ColumnOf(memberSide, root, map);
        var value = Evaluate(valueSide);
        if (value is null)
            return op == ExpressionType.Equal ? q.WhereNull(column)
                 : op == ExpressionType.NotEqual ? q.WhereNotNull(column)
                 : throw new UnsupportedSpecificationException($"Only ==/!= null is supported for '{column}'.");
        return op switch
        {
            ExpressionType.Equal => q.Where(column, value),
            ExpressionType.NotEqual => q.WhereNot(column, value),
            ExpressionType.GreaterThan => q.Where(column, ">", value),
            ExpressionType.GreaterThanOrEqual => q.Where(column, ">=", value),
            ExpressionType.LessThan => q.Where(column, "<", value),
            ExpressionType.LessThanOrEqual => q.Where(column, "<=", value),
            _ => throw new UnsupportedSpecificationException($"Unsupported operator '{op}'.")
        };
    }

    private static (Expression member, Expression value, ExpressionType op) Orient(BinaryExpression b, ParameterExpression root)
    {
        if (RefersTo(b.Left, root))
        {
            if (RefersTo(b.Right, root))
                throw new UnsupportedSpecificationException($"Column-to-column comparisons are not supported ('{b}'). Use provider-native (tier-2) SqlKata.");
            return (b.Left, b.Right, b.NodeType);
        }
        if (RefersTo(b.Right, root)) return (b.Right, b.Left, Flip(b.NodeType));
        throw new UnsupportedSpecificationException($"Comparison '{b}' does not reference the entity.");
    }

    private static Query MethodCall(Query q, MethodCallExpression m, ParameterExpression root, EntityMapping map)
    {
        if (m.Object is not null && RefersTo(m.Object, root) && m.Method.DeclaringType == typeof(string) && IsEntityColumn(m.Object, root))
        {
            var column = ColumnOf(m.Object, root, map);
            var arg = Evaluate(m.Arguments[0])?.ToString() ?? "";
            // LIKE metacharacters ('%', '_') in arg are treated as wildcards (matches EF Core's default
            // Contains/StartsWith/EndsWith semantics). Not auto-escaped: the escape char differs per engine
            // and escaping here would diverge from EF. Callers needing literal matching should use tier-2.
            return m.Method.Name switch
            {
                nameof(string.Contains) => q.WhereLike(column, $"%{arg}%"),
                nameof(string.StartsWith) => q.WhereLike(column, $"{arg}%"),
                nameof(string.EndsWith) => q.WhereLike(column, $"%{arg}"),
                _ => throw new UnsupportedSpecificationException($"Unsupported string method '{m.Method.Name}'.")
            };
        }
        if (m.Method.Name == nameof(Enumerable.Contains))
        {
            var (collectionExpr, memberExpr) = m.Object is null ? (m.Arguments[0], m.Arguments[1]) : (m.Object, m.Arguments[0]);
            collectionExpr = UnwrapSpanConversion(collectionExpr);
            if (IsEntityColumn(memberExpr, root) && !RefersTo(collectionExpr, root))
            {
                var column = ColumnOf(memberExpr, root, map);
                var values = ((IEnumerable)Evaluate(collectionExpr)!).Cast<object?>().ToArray();
                return q.WhereIn(column, values);
            }
        }
        throw new UnsupportedSpecificationException($"Unsupported method call '{m.Method.Name}'. Use provider-native (tier-2) SqlKata.");
    }

    private static bool IsEntityColumn(Expression expr, ParameterExpression root)
        => Unwrap(expr) is MemberExpression { Expression: ParameterExpression p } && p == root;

    private static string ColumnOf(Expression expr, ParameterExpression root, EntityMapping map)
    {
        if (Unwrap(expr) is MemberExpression { Expression: ParameterExpression p } member && p == root)
            return map.Column(member.Member.Name);
        throw new UnsupportedSpecificationException($"Only direct properties of the entity are supported ('{expr}'). Use tier-2 for joins/navigation.");
    }

    private static Expression Unwrap(Expression e) => e is UnaryExpression { NodeType: ExpressionType.Convert } u ? u.Operand : e;

    // On net10 an array/list argument to Contains binds to MemoryExtensions.Contains via an implicit
    // ReadOnlySpan<T> conversion (op_Implicit). The span itself can't be evaluated/boxed, so peel the
    // conversion back to the underlying collection expression before evaluating it for an IN clause.
    private static Expression UnwrapSpanConversion(Expression e)
        => e is MethodCallExpression { Method.Name: "op_Implicit", Object: null, Arguments.Count: 1 } call
            ? call.Arguments[0]
            : e;

    private static bool RefersTo(Expression expr, ParameterExpression root)
    {
        var found = false;
        new ParameterFinder(root, () => found = true).Visit(expr);
        return found;
    }

    private static object? Evaluate(Expression expr)
    {
        if (Unwrap(expr) is ConstantExpression c) return c.Value;
        return Expression.Lambda(Unwrap(expr)).Compile().DynamicInvoke();
    }

    private static ExpressionType Flip(ExpressionType t) => t switch
    {
        ExpressionType.GreaterThan => ExpressionType.LessThan,
        ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
        ExpressionType.LessThan => ExpressionType.GreaterThan,
        ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
        _ => t
    };

    private sealed class ParameterFinder(ParameterExpression target, Action onFound) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node == target) onFound();
            return base.VisitParameter(node);
        }
    }
}
