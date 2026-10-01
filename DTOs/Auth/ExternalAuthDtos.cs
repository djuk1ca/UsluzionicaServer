using System.ComponentModel.DataAnnotations;

namespace UsluzionicaServer.DTOs.Auth;

/// <summary>Aplikacija menja kod iz povratne adrese za tokene.</summary>
public sealed class ExternalExchangeRequest
{
    [Required]
    public string Code { get; set; } = string.Empty;

    /// <summary>PKCE tajna koju je aplikacija izmislila na početku prijave.</summary>
    [Required]
    public string Verifier { get; set; } = string.Empty;
}

/// <summary>
/// Ishod razmene. Dva moguća oblika, razlikuju se po <see cref="Status"/>:
///
///   • <c>ok</c>     — nalog postoji, <see cref="Auth"/> je popunjen
///   • <c>signup</c> — nalog ne postoji, aplikacija otvara „Dovrši nalog"
///                     sa <see cref="SignupToken"/>, imenom i mejlom
/// </summary>
public sealed class ExternalExchangeResponse
{
    public string        Status      { get; set; } = string.Empty;
    public AuthResponse? Auth        { get; set; }
    public string?       SignupToken { get; set; }
    public string?       FullName    { get; set; }
    public string?       Email       { get; set; }
    public string?       Provider    { get; set; }
}

/// <summary>
/// „Dovrši nalog" posle prve prijave preko Google-a ili Facebook-a.
///
/// Nalog se pravi TEK OVDE, a ne pri povratku sa Google-a. Tako ne postoji
/// nalog bez saglasnosti sa politikom privatnosti — korisnik koji odustane na
/// ovom ekranu ne ostavlja za sobom nikakav trag u bazi.
/// </summary>
public sealed class ExternalRegisterRequest
{
    [Required]
    public string SignupToken { get; set; } = string.Empty;

    [Required, StringLength(100, MinimumLength = 2)]
    public string FullName { get; set; } = string.Empty;

    [StringLength(100)]
    public string? City { get; set; }

    [StringLength(20)]
    public string? ReferralCode { get; set; }

    /// <summary>Isto pravilo kao u <see cref="RegisterRequest.AcceptedPolicy"/>.</summary>
    public bool AcceptedPolicy { get; set; }
}
