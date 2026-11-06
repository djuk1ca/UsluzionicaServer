using UsluzionicaServer.Domain.Enums;

namespace UsluzionicaServer.Domain.Entities;

/// <summary>
/// Javljanje uslugodavca na posao. Jedno po (posao, uslugodavac) — ponovno
/// javljanje posle povlačenja ili otkazivanja menja ISTI red nazad u Sent.
/// </summary>
public class JobApplication
{
    public int      Id             { get; set; }
    public int      JobId          { get; set; }
    public string   ProviderUserId { get; set; } = string.Empty;

    /// <summary>Poruka autoru (obavezna, do 500 znakova).</summary>
    public string   Message        { get; set; } = string.Empty;

    /// <summary>Ponuda u dinarima; null = bez ponude.</summary>
    public decimal? OfferAmount    { get; set; }

    public JobApplicationStatus Status { get; set; } = JobApplicationStatus.Sent;

    /// <summary>Razlog otkazivanja koji je autor izabrao (vidi ga i uslugodavac).</summary>
    public string?  CancelReason   { get; set; }

    /// <summary>Uslugodavac se ponovo javio posle otkazivanja — autor ga vidi na vrhu.</summary>
    public bool     IsReapplied    { get; set; }

    public DateTime CreatedAt      { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt      { get; set; } = DateTime.UtcNow;

    public Job             Job      { get; set; } = null!;
    public ApplicationUser Provider { get; set; } = null!;
}
