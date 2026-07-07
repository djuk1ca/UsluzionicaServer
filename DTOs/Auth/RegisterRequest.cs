namespace UsluzionicaServer.DTOs.Auth;

public sealed class RegisterRequest
{
    public string  FullName     { get; set; } = string.Empty;
    public string  Email        { get; set; } = string.Empty;
    public string  Password     { get; set; } = string.Empty;
    public string? ReferralCode { get; set; }  // opcionalno — kad klikne referral link
}
