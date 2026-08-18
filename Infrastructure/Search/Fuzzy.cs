namespace UsluzionicaServer.Infrastructure.Search;

/// <summary>
/// Tolerancija na sitne greške u kucanju: `frizerr`, `frizre`, `vodoinstalter`.
///
/// Radi u dva koraka, jer nijedan sam nije upotrebljiv:
///   1. SQL prefilter po pigeonhole principu — sužava kandidate, ne može
///      promašiti tačan rezultat
///   2. OSA rastojanje in-memory nad tim suženim skupom
/// </summary>
public static class Fuzzy
{
    /// <summary>Skor ispod ovoga se odbacuje — bolje nula rezultata nego smeće.</summary>
    public const double MinScore = 0.45;

    private const int MaxWordLen = 64;

    /// <summary>
    /// Dozvoljeno rastojanje po dužini tokena.
    ///
    /// Kratke reči imaju mali prag namerno: na dužini 4, rastojanje 1 spaja
    /// „kuca", „kuka", „muka", „ruka" — nepovezane pojmove. Tolerancija ima
    /// smisla tek kad reč ima dovoljno konteksta da greška ostane greška.
    /// </summary>
    public static int MaxDistance(int length) => length switch
    {
        <= 3 => 0,   // „sto", „bor" — bez tolerancije
        <= 7 => 1,   // „frizer"(6), „frizerr"(7)
        _    => 2    // „vodoinstalater"(14)
    };

    /// <summary>
    /// Deli token na `d+1` delova za SQL prefilter.
    ///
    /// PIGEONHOLE PRINCIP: ako se `q` i `w` razlikuju za najviše `d` izmena,
    /// a `q` podelimo na `d+1` disjunktnih delova, onda `d` izmena može
    /// „pokvariti" najviše `d` delova — bar jedan deo ostaje netaknut i mora
    /// doslovno postojati u `w`.
    ///
    /// Zato `WHERE SearchTitle LIKE '%fri%' OR LIKE '%zerr%'` ne može
    /// promašiti pravi rezultat. Prefilter je korektan po konstrukciji, a ne
    /// heuristika koja „obično radi".
    ///
    /// Primeri:
    ///   frizerr (7, d=1)       → "fri" + "zerr"   ; „frizer" sadrži "fri" ✓
    ///   frizre  (6, d=1)       → "fri" + "zre"    ; „frizer" sadrži "fri" ✓
    ///   vodoinstalter (13,d=2) → "vodo"+"inst"+"alter" ; „vodoinstalater"
    ///                            sadrži "vodo" i "inst" ✓
    /// </summary>
    public static IReadOnlyList<string> PigeonholeFragments(string token, int maxDistance)
    {
        if (string.IsNullOrEmpty(token)) return [];

        var parts = maxDistance + 1;
        if (parts <= 1) return [token];

        var size = token.Length / parts;

        // Delovi kraći od 3 znaka nisu selektivni — LIKE '%ab%' pogađa pola
        // baze. Tada radije koristimo jedan kratak prefiks.
        if (size < 3)
            return [token[..Math.Min(3, token.Length)]];

        var fragments = new List<string>(parts);
        for (var i = 0; i < parts; i++)
        {
            var start  = i * size;
            var length = i == parts - 1 ? token.Length - start : size;
            fragments.Add(token.Substring(start, length));
        }
        return fragments;
    }

    /// <summary>
    /// Optimal String Alignment — Damerau-Levenshtein bez pravila o
    /// višestrukoj transpoziciji.
    ///
    /// OSA umesto čistog Levenshteina jer je zamena mesta dva susedna slova
    /// („frizre" ↔ „frizer") najčešća greška pri kucanju. Levenshtein je
    /// naplaćuje kao DVE izmene (brisanje + umetanje), pa bi na dužini 6, gde
    /// je prag 1, taj par ispao iz tolerancije. OSA je naplaćuje kao jednu.
    ///
    /// Vraća `max + 1` ako je rastojanje veće od `max` (rani izlaz).
    /// </summary>
    public static int Osa(string a, string b, int max)
    {
        int n = a.Length, m = b.Length;

        // stackalloc ispod je ograničen — vrlo duge reči ne obrađujemo.
        if (n > MaxWordLen || m > MaxWordLen) return max + 1;

        // Razlika dužina je donja granica rastojanja — tačan i jeftin odsek.
        if (Math.Abs(n - m) > max) return max + 1;

        if (n == 0) return m;
        if (m == 0) return n;

        // Tri reda umesto pune matrice: OSA gleda najviše dva reda unazad
        // (prev2 je potreban samo za transpoziciju).
        Span<int> prev2 = stackalloc int[m + 1];
        Span<int> prev  = stackalloc int[m + 1];
        Span<int> cur   = stackalloc int[m + 1];

        for (var j = 0; j <= m; j++) prev[j] = j;

        for (var i = 1; i <= n; i++)
        {
            cur[0] = i;
            var rowMin = cur[0];

            for (var j = 1; j <= m; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;

                var value = Math.Min(
                    Math.Min(cur[j - 1] + 1,     // umetanje
                             prev[j] + 1),       // brisanje
                    prev[j - 1] + cost);         // zamena

                // Transpozicija: susedna dva znaka zamenila mesta.
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    value = Math.Min(value, prev2[j - 2] + 1);

                cur[j] = value;
                if (value < rowMin) rowMin = value;
            }

            // Ako je CEO red iznad praga, rastojanje više ne može pasti ispod
            // njega — nema smisla računati ostatak matrice.
            if (rowMin > max) return max + 1;

            // Rotiraj redove bez alokacije.
            var temp = prev2; prev2 = prev; prev = cur; cur = temp;
        }

        return prev[m];
    }

    /// <summary>
    /// Koliko dobro token odgovara reči: 1.0 = identično, 0.0 = nepovezano.
    /// </summary>
    public static double Similarity(string token, string word)
    {
        if (token == word) return 1.0;
        if (token.Length == 0 || word.Length == 0) return 0.0;

        // Reč koja SADRŽI token je jak pogodak i bez fuzzy računanja
        // („frizer" u „frizerski"). Skor je malo ispod 1 da tačno poklapanje
        // uvek bude iznad.
        if (word.Contains(token, StringComparison.Ordinal))
            return 0.9 * token.Length / word.Length + 0.05;

        var max      = MaxDistance(token.Length);
        if (max == 0) return 0.0;

        var distance = Osa(token, word, max);
        if (distance > max) return 0.0;

        // Rastojanje 0 → 1.0; rastojanje = max → nešto iznad praga.
        return 1.0 - (double)distance / (Math.Max(token.Length, word.Length) + 1);
    }
}
