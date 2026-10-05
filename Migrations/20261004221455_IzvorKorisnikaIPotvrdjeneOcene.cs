using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UsluzionicaServer.Migrations
{
    /// <inheritdoc />
    public partial class IzvorKorisnikaIPotvrdjeneOcene : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AcquisitionSource",
                table: "AspNetUsers",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            // Prosek i broj ocena se čuvaju u ProviderProfiles i preračunavaju
            // tek pri NOVOJ oceni. Od ove izmene se broje samo ocene vezane za
            // izvršenu uslugu (ReviewService.Potvrdjene) — bez ovog preračuna
            // profili bi do prve sledeće ocene pokazivali prosek koji uključuje
            // ocene koje se više ne prikazuju.
            migrationBuilder.Sql(PreracunajProseke(samoPotvrdjene: true));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(PreracunajProseke(samoPotvrdjene: false));

            migrationBuilder.DropColumn(
                name: "AcquisitionSource",
                table: "AspNetUsers");
        }

        private static string PreracunajProseke(bool samoPotvrdjene) => $"""
            UPDATE p SET
                AverageRating = ISNULL(s.Prosek, 0),
                TotalReviews  = ISNULL(s.Broj, 0)
            FROM ProviderProfiles p
            LEFT JOIN (
                SELECT l.ProviderProfileId,
                       ROUND(AVG(CAST(r.Stars AS decimal(18,4))), 2) AS Prosek,
                       COUNT(*) AS Broj
                FROM Reviews r
                JOIN Listings l ON l.Id = r.ListingId
                {(samoPotvrdjene ? "WHERE r.BookingRequestId IS NOT NULL" : "")}
                GROUP BY l.ProviderProfileId
            ) s ON s.ProviderProfileId = p.Id;
            """;
    }
}
