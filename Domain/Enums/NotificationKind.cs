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
    ReferralRewarded
}
