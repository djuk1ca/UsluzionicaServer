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
    AccountDeactivated
}
