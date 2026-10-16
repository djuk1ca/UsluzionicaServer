namespace UsluzionicaServer.Domain.Enums;

public enum NotificationKind
{
    NewMessage,
    BookingReceived,   // provider dobija kad stigne novi zahtev
    BookingConfirmed,  // klijent dobija kad provider potvrdi
    BookingRejected,   // klijent dobija kad provider odbije
    BookingCancelled,  // provider dobija kad klijent otkaže
    TokenEarned,
    NewReview,
    BoostExpiring,
    DiscountOfferReceived,
    DiscountOfferAccepted,
    DiscountOfferRejected,
    ReferralRewarded,

    // ── Moderacija ─────────────────────────────────────────────────────────
    // Dodato NA KRAJ namerno — nove vrednosti između postojećih pomerile bi
    // značenje već upisanih redova ako se enum čuva kao int.
    //
    // Obe se šalju TEK KAD je odluka doneta, nikad na samu prijavu.
    // Obaveštenje „neko te je prijavio" vodi u osvetu između korisnika i uči
    // zloupotrebljivače šta je zapaženo pre nego što iko stigne da pregleda.

    /// <summary>Oglas uklonjen posle prijave. Vlasnik mora da zna i zašto.</summary>
    ListingRemoved,

    /// <summary>Nalog deaktiviran zbog kršenja uslova korišćenja.</summary>
    AccountDeactivated,

    /// <summary>
    /// Provider: isteklo je čekanje posle prihvatanja i usluga sada može da se
    /// označi kao izvršena. Šalje PodsetnikZaIzvrsenje, jednom po zahtevu.
    /// </summary>
    BookingExecutable,

    // ── Poslovi ────────────────────────────────────────────────────────────
    // Referenca je uvek ID POSLA. Tekstovi: Plan Poslova §3.6.

    /// <summary>Autor: neko se javio na posao.</summary>
    JobApplicationReceived,
    /// <summary>Uslugodavac: izabran je za posao.</summary>
    JobChosen,
    /// <summary>Ostali koji su se javili: autor je izabrao drugog.</summary>
    JobNotChosen,
    /// <summary>Izvršilac: autor je otkazao dogovor.</summary>
    JobAssignmentCancelled,
    /// <summary>Ostali koji su se javili: posao je ponovo otvoren.</summary>
    JobReopened,
    /// <summary>Autor: izvršilac je odustao.</summary>
    JobWorkerQuit,
    /// <summary>Svi koji su se javili: autor je obrisao posao.</summary>
    JobRemoved,
    /// <summary>Autor: posao ističe za 24 h.</summary>
    JobExpiringSoon,
    /// <summary>Autor: posao je istekao (7 dana za produženje).</summary>
    JobExpired,
    /// <summary>Autor: prošlo je čekanje, posao može da se označi kao izvršen.</summary>
    JobCompletable,
    /// <summary>Obe strane: posao je izvršen, tokeni isplaćeni.</summary>
    JobCompleted,

    // ── Ocene ──────────────────────────────────────────────────────────────
    /// <summary>
    /// Druga strana te je ocenila — oceni i ti da vidiš ocenu. Referenca je
    /// zahtev (usluga) ili posao; ReferenceType u telu nije potreban jer
    /// klijent vodi na ekran sa svim ocenama koje čekaju.
    /// </summary>
    ReviewRequested
}
