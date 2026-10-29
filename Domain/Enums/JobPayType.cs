namespace UsluzionicaServer.Domain.Enums;

/// <summary>
/// Način isplate posla. Bira se pri objavi i ODMAH određuje kako se iznos
/// unosi i prikazuje: mesečan i dugoročan posao pišu mesečnu platu („80.000 RSD
/// mesečno"), jednokratan samo iznos isplate („3.000 RSD").
///
/// Čuva se kao STRING (vidi AppDbContext), pa redosled vrednosti ne utiče na
/// postojeće redove. Invarijanta (održava JobService): <see cref="Negotiable"/>
/// ⇔ PayAmount == null. Volonterski posao nema isplatu — DTO tada nosi null
/// umesto tipa (vidi JobMapa).
/// </summary>
public enum JobPayType
{
    /// <summary>Jednokratno, isplata po završetku — „Iznos isplate". Podrazumevano.</summary>
    OneTime,

    /// <summary>Mesečno — posao na određeno, plaćen mesečno: „Mesečna plata".</summary>
    Monthly,

    /// <summary>
    /// Dugoročno — stalni angažman. Iznos je mesečna plata: „Dugoročno ·
    /// 80.000 RSD mesečno". Razlika od <see cref="Monthly"/>: posao bez kraja.
    /// </summary>
    LongTerm,

    /// <summary>Po projektu — iznos za ceo posao od početka do kraja.</summary>
    PerProject,

    /// <summary>Po satu — „Satnica", za poslove koji se plaćaju po utrošenom vremenu.</summary>
    Hourly,

    /// <summary>Drugo — iznos uz objašnjenje u opisu.</summary>
    Other,

    /// <summary>Po dogovoru — bez iznosa.</summary>
    Negotiable
}
