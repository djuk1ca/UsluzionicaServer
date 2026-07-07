namespace UsluzionicaServer.Domain.Entities;

public class ServiceExecution
{
    public int      Id               { get; set; }
    public int      BookingRequestId { get; set; }
    public DateTime ExecutedAt       { get; set; } = DateTime.UtcNow;

    // Navigation
    public BookingRequest BookingRequest { get; set; } = null!;
}
