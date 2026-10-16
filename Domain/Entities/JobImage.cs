namespace UsluzionicaServer.Domain.Entities;

/// <summary>Fotografija posla (najviše Jobs:MaxImages). Javna, kao slike oglasa.</summary>
public class JobImage
{
    public int    Id        { get; set; }
    public int    JobId     { get; set; }
    public string ImageUrl  { get; set; } = string.Empty;
    public int    SortOrder { get; set; }

    public Job Job { get; set; } = null!;
}
