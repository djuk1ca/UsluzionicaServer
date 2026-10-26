namespace UsluzionicaServer.DTOs.Jobs;

// ── Javni pregled (gost, bez naloga) ───────────────────────────────────────
//
// Odvojen oblik od JobDto, a ne JobDto sa praznim poljima: šta ovde ne postoji
// ne može ni da procuri kad se JobDto kasnije proširi. Bez UserId-a autora,
// mejla, tačne adrese i svega što zavisi od gledaoca (MyApplication, Assigned,
// IsMine…). Tačno ono što sajt čita (KatalogModeli.Posao).

/// <summary>Posao za gosta — kartica i detalj, <c>/api/public/jobs</c>.</summary>
public sealed class PublicJobDto
{
    public int        Id           { get; set; }
    public string     Title        { get; set; } = string.Empty;
    public string     Description  { get; set; } = string.Empty;
    public string     CategoryName { get; set; } = string.Empty;
    public string     CategorySlug { get; set; } = string.Empty;

    /// <summary>Grad sa zvanične liste — posao nema tačnu adresu.</summary>
    public string     Location     { get; set; } = string.Empty;

    /// <summary>null = „Po dogovoru".</summary>
    public decimal?   PayAmount    { get; set; }

    public DateTime   CreatedAt    { get; set; }
    public DateTime   ExpiresAt    { get; set; }
    public bool       IsBoosted    { get; set; }
    public int        ApplicationsCount { get; set; }

    public List<PublicJobImageDto> Images { get; set; } = [];

    public PublicJobAuthorDto Author { get; set; } = new();
}

public sealed class PublicJobImageDto
{
    public string ImageUrl  { get; set; } = string.Empty;
    public int    SortOrder { get; set; }
}

/// <summary>Autor za gosta — ime skraćeno („Sanja M."), ocena kao naručilac.</summary>
public sealed class PublicJobAuthorDto
{
    public string  FullName    { get; set; } = string.Empty;
    public decimal Rating      { get; set; }
    public int     RatingCount { get; set; }
    public bool    IsVerified  { get; set; }
}

public sealed class PagedPublicJobs
{
    public List<PublicJobDto> Items    { get; set; } = [];
    public int                Total    { get; set; }
    public int                Page     { get; set; }
    public int                PageSize { get; set; }
}

/// <summary>
/// Parametri javne pretrage — isti kao <see cref="JobQueryParams"/>, ali BEZ
/// ViewerUserId: ne postoji polje koje bi model binding mogao da popuni iz upita.
/// </summary>
public sealed class PublicJobQueryParams
{
    public string? Q            { get; set; }
    public string? City         { get; set; }
    public string? CategorySlug { get; set; }

    /// <summary>true = samo istaknuti (sekcija), false = bez istaknutih (lista), null = svi.</summary>
    public bool?   Boosted      { get; set; }

    public int     Page         { get; set; } = 1;
    public int     PageSize     { get; set; } = 20;
}
