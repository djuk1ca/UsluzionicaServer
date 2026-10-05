namespace UsluzionicaServer.DTOs.Users;

/// <summary>
/// Potvrda brisanja naloga — JEDNO od dvoga, zavisno od naloga:
///
///   • nalog sa lozinkom    → <see cref="Password"/>
///   • nalog bez lozinke    → <see cref="Confirmation"/> = „OBRIŠI"
///     (napravljen preko Google-a ili Facebook-a, lozinku nikad nije imao)
///
/// Nijedno polje nije [Required]: koje je obavezno zna tek servis, kad vidi
/// nalog. Atribut bi nalog bez lozinke zauvek ostavio bez načina da se obriše.
/// </summary>
public sealed class DeleteAccountDto
{
    public string? Password     { get; set; }
    public string? Confirmation { get; set; }
}
