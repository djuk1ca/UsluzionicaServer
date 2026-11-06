using UsluzionicaServer.Domain.Enums;

namespace UsluzionicaServer.Domain.Entities;

/// <summary>
/// Ocena jedne strane od druge, posle izvršene usluge ili izvršenog posla.
/// Smer je u podacima: <see cref="SubjectUserId"/> je ocenjeni,
/// <see cref="SubjectRole"/> kaže da li kao uslugodavac ili kao naručilac.
///
/// SKRIVENA DOK OBE STRANE NE OCENE (ili dok ne prođe rok od 14 dana) — da
/// druga strana ne bi ocenjivala „za kaznu" posle loše ocene. Vidi
/// ReviewService.
/// </summary>
public class Review
{
    public int      Id               { get; set; }

    /// <summary>Oglas usluge; null za ocene posla.</summary>
    public int?     ListingId        { get; set; }
    public int?     BookingRequestId { get; set; }

    /// <summary>Posao; postaje null kad se posao trajno obriše (naslov ostaje u <see cref="ContextTitle"/>).</summary>
    public int?     JobId            { get; set; }

    public string   AuthorId         { get; set; } = string.Empty;
    public string   SubjectUserId    { get; set; } = string.Empty;
    public ReviewRole SubjectRole    { get; set; } = ReviewRole.Provider;

    /// <summary>Naslov oglasa ili posla u trenutku ocene.</summary>
    public string   ContextTitle     { get; set; } = string.Empty;

    public int      Stars            { get; set; }
    public string?  Comment          { get; set; }
    public DateTime CreatedAt        { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Nastala uz izvršenu uslugu ili posao. Ocene od pre pravila „samo uz
    /// izvršenu uslugu" nemaju ovo i ne prikazuju se. Zasebna kolona jer
    /// JobId posle trajnog brisanja posla postaje null.
    /// </summary>
    public bool     IsVerified       { get; set; }

    /// <summary>Kad je postala vidljiva; null = čeka drugu stranu ili rok.</summary>
    public DateTime? PublishedAt     { get; set; }

    /// <summary>Kraj roka za ocenu — tada se objavljuje i bez druge strane.</summary>
    public DateTime RevealAt         { get; set; }

    // Navigation
    public Listing?        Listing        { get; set; }
    public BookingRequest? BookingRequest { get; set; }
    public Job?            Job            { get; set; }
    public ApplicationUser Author         { get; set; } = null!;
    public ApplicationUser Subject        { get; set; } = null!;
}
