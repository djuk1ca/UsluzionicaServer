using System.Globalization;

namespace UsluzionicaServer.Infrastructure;

/// <summary>
/// Kratak pregled teksta (lista razgovora, obaveštenje, push).
///
/// Seče po GRAFEMIMA, ne po char-ovima. Ranije je stajalo <c>text[..60]</c>:
/// emoji je u .NET-u dva char-a (surogat par), pa je rez baš na njemu ostavljao
/// pola znaka — u pregledu se videlo „�", a push servis je ponekad odbijao
/// takav tekst kao neispravan UTF-16.
/// </summary>
public static class Pregled
{
    public static string Skrati(string? tekst, int najvise)
    {
        if (string.IsNullOrEmpty(tekst)) return string.Empty;

        var info = new StringInfo(tekst);
        if (info.LengthInTextElements <= najvise) return tekst;

        return info.SubstringByTextElements(0, najvise).TrimEnd() + "…";
    }
}
