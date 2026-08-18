using System.Globalization;
using System.Text;

namespace UsluzionicaServer.Infrastructure.Search;

/// <summary>
/// Svodi tekst na oblik pogodan za pretragu: mala slova, čist ASCII, bez
/// dijakritike, ćirilica preslovljena u latinicu.
///
/// Cilj je da upit i sadržaj oglasa završe u ISTOM obliku, pa da:
///   "sisanje"  nađe  "Šišanje"
///   "фризер"   nađe  "Frizerski salon"
///   "Đorđe"    nađe  "djordje"
///
/// Ova klasa je jedini izvor istine za preklapanje. Isti Fold() se koristi i
/// pri upisu u bazu (denormalizovane Search* kolone) i pri obradi upita —
/// da se dve strane ne mogu razići.
/// </summary>
public static class SearchNormalizer
{
    /// <summary>
    /// Verzija pravila preklapanja. Podigni je kad promeniš mapu ili logiku —
    /// backfill pri sledećem pokretanju automatski re-indeksira sve redove
    /// kojima se SearchVersion ne poklapa. Zato ta kolona postoji.
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// Eksplicitna mapa. NIJE opciona — vidi komentar u Fold() o tome zašto
    /// Unicode dekompozicija nije dovoljna.
    /// </summary>
    private static readonly Dictionary<char, string> Map = new()
    {
        // ── Srpska latinica ────────────────────────────────────────────────
        ['č'] = "c", ['Č'] = "c",
        ['ć'] = "c", ['Ć'] = "c",
        ['š'] = "s", ['Š'] = "s",
        ['ž'] = "z", ['Ž'] = "z",
        // đ se preslovljava u DVA znaka — otud string, a ne char, kao vrednost
        ['đ'] = "dj", ['Đ'] = "dj",

        // ── Srpska ćirilica ───────────────────────────────────────────────
        ['а'] = "a",  ['А'] = "a",
        ['б'] = "b",  ['Б'] = "b",
        ['в'] = "v",  ['В'] = "v",
        ['г'] = "g",  ['Г'] = "g",
        ['д'] = "d",  ['Д'] = "d",
        ['ђ'] = "dj", ['Ђ'] = "dj",
        ['е'] = "e",  ['Е'] = "e",
        ['ж'] = "z",  ['Ж'] = "z",
        ['з'] = "z",  ['З'] = "z",
        ['и'] = "i",  ['И'] = "i",
        ['ј'] = "j",  ['Ј'] = "j",
        ['к'] = "k",  ['К'] = "k",
        ['л'] = "l",  ['Л'] = "l",
        ['љ'] = "lj", ['Љ'] = "lj",
        ['м'] = "m",  ['М'] = "m",
        ['н'] = "n",  ['Н'] = "n",
        ['њ'] = "nj", ['Њ'] = "nj",
        ['о'] = "o",  ['О'] = "o",
        ['п'] = "p",  ['П'] = "p",
        ['р'] = "r",  ['Р'] = "r",
        ['с'] = "s",  ['С'] = "s",
        ['т'] = "t",  ['Т'] = "t",
        ['ћ'] = "c",  ['Ћ'] = "c",
        ['у'] = "u",  ['У'] = "u",
        ['ф'] = "f",  ['Ф'] = "f",
        ['х'] = "h",  ['Х'] = "h",
        ['ц'] = "c",  ['Ц'] = "c",
        ['ч'] = "c",  ['Ч'] = "c",
        ['џ'] = "dz", ['Џ'] = "dz",
        ['ш'] = "s",  ['Ш'] = "s",
    };

    /// <summary>
    /// Svodi tekst na foldovani oblik. Vraća prazan string za null/prazno.
    /// </summary>
    public static string Fold(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        // ── KORAK 1: NFD dekompozicija ─────────────────────────────────────
        // Razlaže komponovane znakove na osnovno slovo + dijakritički znak,
        // koji se odbacuje u koraku 3. Time č/ć/š/ž prolaze bez ulaska u mapu,
        // a strani znakovi (é → e, ü → u, ā → a) dolaze besplatno.
        //
        // O redosledu NFD-pre-malih-slova: to je konvencija, ne nužnost u OVOJ
        // implementaciji. Pošto petlja ispod svaki znak provlači kroz
        // char.ToLowerInvariant, oba redosleda daju isti rezultat čak i za
        // 'İ' (U+0130). Provereno empirijski:
        //     NFD pa lower  →  U+0069 U+0307  →  "istanbul"
        //     lower pa NFD  →  U+0049 U+0307  →  "istanbul"  (I se snizi u petlji)
        // Redosled je zadržan jer je otporniji ako se petlja ikad promeni.
        var decomposed = input.Normalize(NormalizationForm.FormD);

        var sb = new StringBuilder(decomposed.Length + 8);

        foreach (var ch in decomposed)
        {
            // ── KORAK 2: eksplicitna mapa ──────────────────────────────────
            // Ovo NIJE višak posla uprkos NFD-u. Provereno:
            //   đ (U+0111) — NFD ga NE razlaže, nije komponovan znak
            //   Đ (U+0110) — isto
            //   ђ, ћ, џ, ж, ч, ш i sva ostala ćirilica — NFD ih ne dira
            // Bez mape bi svi ti znakovi ispali kroz whitelist ispod i tiho
            // nestali iz teksta.
            if (Map.TryGetValue(ch, out var replacement))
            {
                sb.Append(replacement);
                continue;
            }

            // ── KORAK 3: odbaci dijakritičke znakove ───────────────────────
            // Ono što je NFD odvojio od č/ć/š/ž/é/ü — osnovno slovo je već
            // prošlo, ovaj combining znak se odbacuje.
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            // ── KORAK 4: mala slova, invarijantno ──────────────────────────
            // ToLowerInvariant, NIKAD ToLower(). Pod tr-TR kulturom
            // "IZNAJMLJIVANJE".ToLower() daje 'ı' (U+0131, beztačkasto i),
            // koje bi whitelist ispod obrisao → "znajmljvanje".
            var lower = char.ToLowerInvariant(ch);

            // ── KORAK 5: whitelist ─────────────────────────────────────────
            // Sve što nije ASCII slovo ili cifra postaje razmak. Time
            // interpunkcija, crte i em-crta u imenima opština ("Beograd —
            // Vračar") prirodno razdvajaju tokene.
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9')
                sb.Append(lower);
            else
                sb.Append(' ');
        }

        // ── KORAK 6: sažmi razmake ─────────────────────────────────────────
        return CollapseWhitespace(sb);
    }

    /// <summary>Deli foldovan tekst na tokene.</summary>
    public static string[] Tokenize(string? input) =>
        Fold(input).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Varijante tokena zbog dvosmislenosti slova „đ".
    ///
    /// Korisnik ga kuca na tri načina: `Đorđe`, `Djordje`, `Dordje`. Prva dva
    /// se foldiraju u "djordje", treći u "dordje" — različiti stringovi.
    /// Zato upit traži uniju obe varijante.
    ///
    /// Vraća 1 ili 2 stavke; nikad prazno.
    /// </summary>
    public static IReadOnlyList<string> DjVariants(string foldedToken)
    {
        if (string.IsNullOrEmpty(foldedToken)) return [foldedToken];

        // "djordje" → i "dordje" (za korisnika koji je otkucao "Dordje")
        if (foldedToken.Contains("dj", StringComparison.Ordinal))
        {
            var withoutJ = foldedToken.Replace("dj", "d", StringComparison.Ordinal);
            if (withoutJ != foldedToken) return [foldedToken, withoutJ];
        }

        return [foldedToken];
    }

    /// <summary>
    /// Escape-uje znakove koje SQL Server LIKE tretira kao specijalne.
    ///
    /// Bez ovoga upit "%" vraća SVE oglase, a "[a-z]" se tumači kao opseg.
    /// Prati se sa ESCAPE '\' u samom LIKE izrazu.
    /// </summary>
    public static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\")
             .Replace("%",  "\\%")
             .Replace("_",  "\\_")
             .Replace("[",  "\\[");

    private static string CollapseWhitespace(StringBuilder sb)
    {
        var result   = new StringBuilder(sb.Length);
        var lastWasSpace = true;   // true na početku → vodeći razmaci se gutaju

        for (var i = 0; i < sb.Length; i++)
        {
            var ch = sb[i];
            if (ch == ' ')
            {
                if (!lastWasSpace) result.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                result.Append(ch);
                lastWasSpace = false;
            }
        }

        // Skini eventualni prateći razmak.
        if (result.Length > 0 && result[^1] == ' ')
            result.Length--;

        return result.ToString();
    }
}
