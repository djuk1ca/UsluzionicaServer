using System.ComponentModel.DataAnnotations;

namespace UsluzionicaServer.DTOs.Auth;

public sealed class ForgotPasswordRequest
{
    [Required(ErrorMessage = "Unesi email adresu.")]
    [EmailAddress(ErrorMessage = "Email adresa nije ispravna.")]
    public string Email { get; set; } = string.Empty;
}

public sealed class ResetPasswordRequest
{
    [Required(ErrorMessage = "Unesi email adresu.")]
    [EmailAddress(ErrorMessage = "Email adresa nije ispravna.")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Unesi kod iz emaila.")]
    [RegularExpression(@"^\d{6}$", ErrorMessage = "Kod mora imati tačno 6 cifara.")]
    public string Code { get; set; } = string.Empty;

    [Required(ErrorMessage = "Unesi novu lozinku.")]
    [MinLength(8, ErrorMessage = "Lozinka mora imati najmanje 8 karaktera.")]
    public string NewPassword { get; set; } = string.Empty;
}
