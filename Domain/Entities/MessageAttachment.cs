namespace UsluzionicaServer.Domain.Entities;

/// <summary>
/// Slika poslata u četu. Živi tačno koliko i poruka (14 dana), šifrovana na
/// disku van wwwroot-a — vidi <see cref="Infrastructure.ChatMedia.ChatMediaStore"/>.
/// Jedna poruka ima najviše jedan prilog.
/// </summary>
public class MessageAttachment
{
    /// <summary>Ujedno i ime fajla — ne može da se pogodi.</summary>
    public Guid      Id          { get; set; } = Guid.NewGuid();
    public int       MessageId   { get; set; }

    /// <summary>„yyyy/MM/dd/{Id}.bin", relativno na ChatMedia:Path.</summary>
    public string    StoragePath { get; set; } = string.Empty;

    /// <summary>Za rezervisanje mesta u balonu (bez skakanja liste).</summary>
    public int       Width       { get; set; }
    public int       Height      { get; set; }
    public long      SizeBytes   { get; set; }

    /// <summary>Automatska provera nije sigurna — kod primaoca je zamućena sa „Prikaži".</summary>
    public bool      NeedsReview { get; set; }

    /// <summary>Zadržano zbog prijave — čišćenje posle 14 dana ga preskače do ovog trenutka.</summary>
    public DateTime? HeldUntil   { get; set; }

    public DateTime  CreatedAt   { get; set; } = DateTime.UtcNow;

    public Message   Message     { get; set; } = null!;
}
