using System.ComponentModel.DataAnnotations;

namespace UsluzionicaServer.DTOs.Tokens;

/// <summary>Trenutni token balans korisnika.</summary>
public sealed class BalanceDto
{
    public string  UserId  { get; set; } = string.Empty;
    public decimal Balance { get; set; }
}

/// <summary>Jedan zapis u token ledgeru.</summary>
public sealed class TransactionDto
{
    public int      Id           { get; set; }

    /// <summary>Pozitivan = prihod, negativan = rashod.</summary>
    public decimal  Amount       { get; set; }

    /// <summary>ServiceReward | BoostSpend | DiscountSent | DiscountReceived | Referral | Purchase</summary>
    public string   Kind         { get; set; } = string.Empty;

    public string   Description  { get; set; } = string.Empty;
    public decimal  BalanceAfter { get; set; }
    public DateTime CreatedAt    { get; set; }
    public int?     ReferenceId  { get; set; }
}

/// <summary>Body za POST /api/listings/{id}/boost.</summary>
public sealed class BoostListingDto
{
    /// <summary>Broj tokena koji se troše. Mora biti > 0.</summary>
    [Required]
    [Range(0.01, double.MaxValue, ErrorMessage = "Broj tokena mora biti pozitivan.")]
    public decimal TokensToSpend { get; set; }

    /// <summary>Trajanje boosta u danima. Dozvoljene vrednosti: 3, 7, 14.</summary>
    [Required]
    public int DurationDays { get; set; }
}
