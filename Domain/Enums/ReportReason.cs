namespace UsluzionicaServer.Domain.Enums;

/// <summary>
/// Razlog prijave. Zatvorena lista, ne slobodan tekst.
///
/// Razlog za zatvorenu listu: prijave se grupišu i rangiraju (vidi
/// <c>ReportService.GetQueueAsync</c>), a slobodan tekst se ne može grupisati.
/// Uz to, ponuđeni razlozi vode prijavioca ka korisnoj prijavi — „Spam" i
/// „Prevara" traže različitu reakciju, a bez liste bi obe stigle kao „loš oglas".
///
/// Napomena uz prijavu i dalje postoji (<c>Report.Note</c>), ali kao dopuna.
/// </summary>
public enum ReportReason
{
    /// <summary>Nudity, uvredljiv, mrzilački ili nasilan sadržaj.</summary>
    Neprikladno,

    /// <summary>Lažna usluga, pokušaj prevare, obmanjujuća cena.</summary>
    Prevara,

    /// <summary>Reklama, ponavljanje istog oglasa, sadržaj nevezan za uslugu.</summary>
    Spam,

    /// <summary>Oglas je u kategoriji kojoj ne pripada.</summary>
    PogresnaKategorija,

    /// <summary>Usluga koja je protivzakonita ili zabranjena uslovima korišćenja.</summary>
    ZabranjenaUsluga,

    /// <summary>Sve ostalo — tada je <c>Note</c> jedini izvor konteksta.</summary>
    Drugo
}
