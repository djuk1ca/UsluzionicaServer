namespace UsluzionicaServer.Domain.Entities;

public class ProviderCategory
{
    public int ProviderProfileId { get; set; }
    public int CategoryId        { get; set; }

    // Navigation
    public ProviderProfile ProviderProfile { get; set; } = null!;
    public Category        Category        { get; set; } = null!;
}
