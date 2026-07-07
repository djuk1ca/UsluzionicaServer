namespace UsluzionicaServer.DTOs.Auth;

/// <summary>
/// Vraća se nakon uspešnog login-a ili refresh-a.
/// </summary>
public sealed class AuthResponse
{
    public string AccessToken  { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public UserDto User        { get; set; } = null!;
}

public sealed class UserDto
{
    public string       Id             { get; set; } = string.Empty;
    public string       FullName       { get; set; } = string.Empty;
    public string       Email          { get; set; } = string.Empty;
    public decimal      TokenBalance   { get; set; }
    public bool         IsProvider     { get; set; }
    public bool         IsAdmin        { get; set; }
    public List<string> Roles          { get; set; } = [];
    public string?      LastKnownCity  { get; set; }
    public string?      ReferralCode   { get; set; }
}
