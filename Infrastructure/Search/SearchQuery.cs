namespace UsluzionicaServer.Infrastructure.Search;

/// <summary>
/// Pripremljen korisnički upit: foldovan, razložen na tokene, sa varijantama
/// i sa fragmentima za fuzzy prefilter.
///
/// Priprema se JEDNOM po zahtevu, pa se koristi u više slojeva pretrage —
/// da se isti posao ne ponavlja za svaki sloj.
/// </summary>
public sealed class SearchQuery
{
    /// <summary>Jedan token upita sa svime što slojevi pretrage traže od njega.</summary>
    public sealed record Token(
        string                 Value,
        IReadOnlyList<string>  Variants,
        int                    MaxDistance,
        IReadOnlyList<string>  Fragments);

    public IReadOnlyList<Token> Tokens { get; }

    /// <summary>Ceo foldovan upit kao jedan string (za poređenje celom frazom).</summary>
    public string Folded { get; }

    public bool IsEmpty => Tokens.Count == 0;

    private SearchQuery(IReadOnlyList<Token> tokens, string folded)
    {
        Tokens = tokens;
        Folded = folded;
    }

    public static SearchQuery Parse(string? raw)
    {
        var folded = SearchNormalizer.Fold(raw);
        if (folded.Length == 0)
            return new SearchQuery([], string.Empty);

        var tokens = folded
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            // Jednoslovni tokeni ("i", "a", "u") ne nose signal, a drastično
            // šire skup rezultata. Izbacuju se — osim ako je ceo upit takav.
            .Where(t => t.Length > 1)
            .Distinct()
            .Take(8)   // gornja granica: 8 tokena je već vrlo specifičan upit
            .Select(BuildToken)
            .ToList();

        // Ako je korisnik otkucao samo "a" ili "u", nema smisla vratiti prazno —
        // radije zadrži taj jedan token.
        if (tokens.Count == 0)
        {
            tokens = folded.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                           .Take(1).Select(BuildToken).ToList();
        }

        return new SearchQuery(tokens, folded);
    }

    private static Token BuildToken(string value)
    {
        var maxDistance = Fuzzy.MaxDistance(value.Length);

        return new Token(
            Value:       value,
            Variants:    SearchNormalizer.DjVariants(value),
            MaxDistance: maxDistance,
            Fragments:   Fuzzy.PigeonholeFragments(value, maxDistance));
    }
}
