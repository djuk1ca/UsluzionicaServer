namespace UsluzionicaServer.DTOs.Auth;

public sealed class RegisterRequest
{
    public string  FullName     { get; set; } = string.Empty;
    public string  Email        { get; set; } = string.Empty;
    public string  Password     { get; set; } = string.Empty;

    // Grad bira sam korisnik pri registraciji, i menja ga kad hoće u profilu.
    // Ranije se izvodio iz IP adrese preko spoljnog servisa — vidi objašnjenje
    // uz uklanjanje geolokacije u AuthService.LoginAsync.
    public string? City         { get; set; }

    public string? ReferralCode { get; set; }  // opcionalno — kad klikne referral link
}
