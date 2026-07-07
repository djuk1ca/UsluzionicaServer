namespace UsluzionicaServer.Domain.Entities;

public class FavoriteProvider
{
    public int      Id                { get; set; }
    public string   UserId            { get; set; } = string.Empty;
    public int      ProviderProfileId { get; set; }
    public DateTime CreatedAt         { get; set; } = DateTime.UtcNow;

    // Navigation
    public ApplicationUser User            { get; set; } = null!;
    public ProviderProfile ProviderProfile { get; set; } = null!;
}
