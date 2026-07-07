using UsluzionicaServer.Domain.Enums;

namespace UsluzionicaServer.Domain.Entities;

public class Notification
{
    public int              Id            { get; set; }
    public string           UserId        { get; set; } = string.Empty;
    public NotificationKind Kind          { get; set; }
    public string           Title         { get; set; } = string.Empty;
    public string           Body          { get; set; } = string.Empty;
    public string?          ReferenceType { get; set; }
    public int?             ReferenceId   { get; set; }
    public bool             IsRead        { get; set; } = false;
    public DateTime         CreatedAt     { get; set; } = DateTime.UtcNow;

    // Navigation
    public ApplicationUser User { get; set; } = null!;
}
