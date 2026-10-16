namespace UsluzionicaServer.Domain.Enums;

/// <summary>
/// Stanje javljanja na posao. Čuva se kao string.
///
/// „Izabran je drugi" se NE čuva — izvodi se iz stanja posla (Dogovoren, a
/// izabrano javljanje nije moje). Zato ponovno otvaranje posla ne mora da dira
/// tuđa javljanja: ona su i dalje <see cref="Sent"/>.
/// </summary>
public enum JobApplicationStatus
{
    /// <summary>Poslato — čeka autorov izbor.</summary>
    Sent,

    /// <summary>Izabrano — ovaj uslugodavac radi posao.</summary>
    Chosen,

    /// <summary>Uslugodavac je povukao javljanje pre izbora.</summary>
    Withdrawn,

    /// <summary>Autor je otkazao dogovor sa ovim uslugodavcem.</summary>
    Cancelled,

    /// <summary>Izabrani uslugodavac je odustao od posla.</summary>
    Quit
}
