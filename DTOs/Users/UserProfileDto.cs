namespace UsluzionicaServer.DTOs.Users;

/// <summary>Vraća se na GET /api/users/me i GET /api/users/{id}</summary>
public sealed class UserProfileDto
{
    public string   Id              { get; set; } = string.Empty;
    public string   FullName        { get; set; } = string.Empty;
    public string   Email           { get; set; } = string.Empty;
    public string?  ProfileImageUrl { get; set; }
    public decimal  TokenBalance    { get; set; }
    public bool     IsProvider      { get; set; }
    public string?  LastKnownCity   { get; set; }
    public string?  ReferralCode    { get; set; }
    public DateTime CreatedAt       { get; set; }
}
