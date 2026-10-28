namespace UsluzionicaServer.Infrastructure;

/// <summary>
/// Verzija politike privatnosti koja je trenutno na snazi.
///
/// ZAŠTO KONSTANTA A NE DATUM IZMENE FAJLA: politika živi na sajtu
/// (usluzionica.rs/policy), van ovog repoa. Server ne može da zna kad se
/// promenila — mora mu se reći.
///
/// Oblik je „GGGG-MM". Dovoljno grubo da se ne menja zbog ispravke tipfelera,
/// dovoljno precizno da se zna o kojoj je verziji reč.
///
/// KAD SE POLITIKA SUŠTINSKI IZMENI:
///   1. promeni ovu konstantu
///   2. korisnici sa starijom verzijom se prepoznaju upitom:
///        WHERE PolicyVersionAccepted IS NULL OR PolicyVersionAccepted &lt;&gt; '2026-09'
///   3. njima se pri sledećoj prijavi traži nova saglasnost
///
/// Korak 3 još nije implementiran — dolazi kad politika prvi put bude menjana.
/// Do tada se vrednost samo beleži, što je dovoljno da se saglasnost dokaže.
///
/// ISTORIJA:
///   2026-09  uslovi korišćenja i politika privatnosti
///   2026-10  isto, plus izjava „imam najmanje 18 godina" u istom checkbox-u
///            (Uslovi, odeljak 3, i Politika, odeljak 10, traže 18+)
/// </summary>
public static class PolicyVersion
{
    public const string Current = "2026-10";

    /// <summary>
    /// Verzija za registraciju koja NIJE potvrdila punoletstvo — stari klijenti
    /// koji ne šalju <c>ConfirmedAdult</c>. Ne odbijaju se, jer bi registracija
    /// iz verzije aplikacije koja je već u prodavnici prestala da radi; ali se
    /// upisuje starija oznaka, da zapis ne tvrdi nešto što korisnik nije video.
    /// Kad stare verzije nestanu, <c>ConfirmedAdult</c> postaje obavezan.
    /// </summary>
    public const string BezPotvrdeUzrasta = "2026-09";

    public const string SamoPunoletni = "Uslužionica je samo za punoletne (18+).";

    /// <summary>„2026-10" (uslovi + izjava o punoletstvu) ili „2026-09" (samo uslovi).</summary>
    public static string Za(bool? potvrdioPunoletstvo) =>
        potvrdioPunoletstvo == true ? Current : BezPotvrdeUzrasta;
}
