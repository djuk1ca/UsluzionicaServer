namespace UsluzionicaServer.DTOs.Auth;

public sealed class VerifyEmailRequest
{
    public string UserId { get; set; } = string.Empty;
    public string Token  { get; set; } = string.Empty;
}
