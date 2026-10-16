namespace UsluzionicaServer.Domain.Enums;

/// <summary>
/// Stanje posla. Čuva se kao STRING (vidi AppDbContext), pa redosled vrednosti
/// ne utiče na postojeće redove.
///
///     Open ──izbor──→ Assigned ──„izvršen" (7 dana)──→ Completed
///      ↑  └──30 dana──→ Expired      │
///      └──── otkazivanje / odustajanje
///
/// Deleted i Completed su „nestali" poslovi: ne vide se nigde, a red ostaje
/// kratko zbog ocena i prijava — vidi JobCleanupService.
/// </summary>
public enum JobStatus
{
    /// <summary>Aktivan — vide ga svi uslugodavci, prima javljanja.</summary>
    Open,

    /// <summary>Dogovoren — autor je izabrao izvršioca; skinut sa pretrage.</summary>
    Assigned,

    /// <summary>Istekao — prošlo 30 dana; autor ima 7 dana da ga produži.</summary>
    Expired,

    /// <summary>Izvršen — tokeni isplaćeni, posao nestaje.</summary>
    Completed,

    /// <summary>Obrisan od strane autora (ili uz brisanje naloga).</summary>
    Deleted
}
