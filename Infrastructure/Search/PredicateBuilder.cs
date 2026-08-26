using System.Linq.Expressions;

namespace UsluzionicaServer.Infrastructure.Search;

/// <summary>
/// Spaja predikate sa OR u jedno izraz-stablo koje EF Core ume da prevede u SQL.
///
/// Zašto uopšte treba: uzastopni `.Where(...)` pozivi daju AND. Slojevi 2 i 3
/// pretrage traže OR preko promenljivog broja tokena/fragmenata, a to se ne
/// može napisati kao `tokens.Any(t => EF.Functions.Like(col, t))` — EF ne ume
/// da prevede `Any` nad kolekcijom iz memorije.
///
/// Alternativa bi bila jedan SQL upit po tokenu pa unija u memoriji (do 8
/// dodatnih round-trip-ova), ili LINQKit kao dodatni paket. Ovih tridesetak
/// linija drži pretragu na jednom upitu, bez nove zavisnosti.
/// </summary>
internal static class PredicateBuilder
{
    /// <summary>
    /// Vraća `a OR b`. Oba predikata moraju biti nad istim tipom.
    /// Parametri se ujednačavaju — dva lambda izraza imaju različite instance
    /// parametra, pa se telo drugog prepisuje da koristi parametar prvog.
    /// </summary>
    public static Expression<Func<T, bool>> Or<T>(
        this Expression<Func<T, bool>> a,
        Expression<Func<T, bool>>      b)
    {
        var parameter = Expression.Parameter(typeof(T), "x");

        var left  = new ParameterReplacer(a.Parameters[0], parameter).Visit(a.Body)!;
        var right = new ParameterReplacer(b.Parameters[0], parameter).Visit(b.Body)!;

        return Expression.Lambda<Func<T, bool>>(Expression.OrElse(left, right), parameter);
    }

    /// <summary>
    /// Spaja sve predikate sa OR. Vraća null za praznu kolekciju — pozivalac
    /// tada preskače `.Where(...)` umesto da doda uslov koji je uvek netačan.
    /// </summary>
    public static Expression<Func<T, bool>>? OrAll<T>(IEnumerable<Expression<Func<T, bool>>> predicates)
    {
        Expression<Func<T, bool>>? combined = null;

        foreach (var predicate in predicates)
            combined = combined is null ? predicate : combined.Or(predicate);

        return combined;
    }

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to)
        : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == from ? to : base.VisitParameter(node);
    }
}
