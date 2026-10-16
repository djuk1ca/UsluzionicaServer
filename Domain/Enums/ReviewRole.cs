namespace UsluzionicaServer.Domain.Enums;

/// <summary>
/// U kojoj ulozi je ocenjeni dobio ocenu. Čuva se kao STRING.
///
/// Provider — kao uslugodavac (posle usluge ili kao izvršilac posla); ulazi u
/// ProviderProfile.AverageRating.
/// Client — kao naručilac (korisnik usluge ili autor posla); ulazi u
/// ApplicationUser.ClientRating.
/// </summary>
public enum ReviewRole
{
    Provider,
    Client
}
