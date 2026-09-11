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
/// </summary>
public static class PolicyVersion
{
    public const string Current = "2026-09";
}
