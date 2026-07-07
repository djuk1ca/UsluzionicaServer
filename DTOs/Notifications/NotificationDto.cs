namespace UsluzionicaServer.DTOs.Notifications;

public sealed class NotificationDto
{
    public int      Id          { get; init; }
    public string   Kind        { get; init; } = string.Empty;
    public string   Title       { get; init; } = string.Empty;
    public string   Body        { get; init; } = string.Empty;
    public int?     ReferenceId { get; init; }
    public bool     IsRead      { get; init; }
    public DateTime CreatedAt   { get; init; }
}
