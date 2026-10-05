namespace UsluzionicaServer.Infrastructure.ExternalAuth;

/// <summary>
/// Ko je korisnik, onako kako ga je predstavio Google ili Facebook.
///
/// Namerno bez tokena provajdera: posle razmene koda ništa nam od njih ne
/// treba, a token koji ne čuvamo ne može ni da procuri.
/// </summary>
/// <param name="Provider">
/// „Google" ili „Facebook" — ISTO ime ide u <c>AspNetUserLogins.LoginProvider</c>.
/// Promena ovog imena bi sve postojeće veze učinila nevidljivim, pa bi se
/// korisnicima pri sledećoj prijavi pravili novi nalozi.
/// </param>
/// <param name="ProviderKey">Stalni id korisnika kod provajdera. Mejl nije
/// ključ — korisnik može da ga promeni kod Google-a, a nalog mora ostati isti.</param>
/// <param name="EmailVerified">
/// Da li provajder GARANTUJE da je mejl potvrđen. Samo tada sme da se poveže sa
/// postojećim nalogom po mejlu — inače bi ko god napravi nalog kod provajdera
/// sa tuđim mejlom preuzeo tuđi nalog kod nas.
/// </param>
/// <param name="PictureUrl">
/// Adresa profilne slike kod provajdera. Služi SAMO da se slika jednom preuzme
/// na naš server — ne čuva se i ne prikazuje dalje (vidi ProfilnaSlikaProvajdera).
/// </param>
public sealed record ExternalIdentity(
    string  Provider,
    string  ProviderKey,
    string? Email,
    bool    EmailVerified,
    string  Name,
    string? PictureUrl = null);

/// <summary>
/// Provajder je odbio razmenu ili vratio nešto neočekivano. Poruka ide u log,
/// korisnik vidi opštu grešku — detalji provajdera ne pomažu korisniku, a
/// mogu da pomognu napadaču.
/// </summary>
public sealed class ExternalAuthException(string message, Exception? inner = null)
    : Exception(message, inner);
