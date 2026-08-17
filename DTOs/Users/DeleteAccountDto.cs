using System.ComponentModel.DataAnnotations;

namespace UsluzionicaServer.DTOs.Users;

public sealed class DeleteAccountDto
{
    [Required(ErrorMessage = "Unesi lozinku da potvrdiš brisanje naloga.")]
    public string Password { get; set; } = string.Empty;
}
