namespace UsluzionicaServer.Domain.Enums;

/// <summary>Stanje jedne prijave.</summary>
public enum ReportStatus
{
    /// <summary>Čeka pregled. Samo ove ulaze u red za admina.</summary>
    Pending,

    /// <summary>Admin je prihvatio prijavu — sadržaj je uklonjen ili nalog deaktiviran.</summary>
    Accepted,

    /// <summary>Admin je pregledao i utvrdio da nema prekršaja.</summary>
    Rejected
}
