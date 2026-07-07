using System.ComponentModel.DataAnnotations;

namespace UsluzionicaServer.DTOs.DiscountOffers;

/// <summary>Response DTO za token discount ponudu.</summary>
public sealed class DiscountOfferDto
{
    public int       Id            { get; set; }

    public string    SenderId      { get; set; } = string.Empty;
    public string    SenderName    { get; set; } = string.Empty;

    public string    ReceiverId    { get; set; } = string.Empty;
    public string    ReceiverName  { get; set; } = string.Empty;

    public int       ListingId     { get; set; }
    public string    ListingTitle  { get; set; } = string.Empty;

    public decimal   TokenAmount   { get; set; }

    /// <summary>Pending | Accepted | Rejected | Cancelled</summary>
    public string    Status        { get; set; } = string.Empty;

    public DateTime  CreatedAt     { get; set; }
    public DateTime? RespondedAt   { get; set; }
}

/// <summary>Body za POST /api/discount-offers.</summary>
public sealed class CreateDiscountOfferDto
{
    [Required]
    public string  ReceiverId     { get; set; } = string.Empty;

    [Required]
    public int     ListingId      { get; set; }

    [Range(0.01, double.MaxValue, ErrorMessage = "Iznos tokena mora biti pozitivan.")]
    public decimal TokenAmount    { get; set; }

    /// <summary>Opcionalno — ako se ponuda šalje unutar chat konverzacije.</summary>
    public int?    ConversationId { get; set; }
}
