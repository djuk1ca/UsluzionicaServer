using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UsluzionicaServer.Migrations
{
    /// <summary>
    /// Migracija PODATAKA (bez izmene šeme).
    ///
    /// Ranije su se u bazu upisivali puni URL-ovi slika ("https://localhost:7176/uploads/...").
    /// Time je domen razvojne mašine bio zapečen u podatke — prvo puštanje na pravi
    /// domen polomilo bi svaku postojeću sliku. Prelazimo na relativne putanje
    /// ("/uploads/..."), a pun URL se sastavlja pri serijalizaciji odgovora
    /// (MediaUrlJsonModifier).
    ///
    /// Rez se radi na "/uploads/" — uzima se sve od tog mesta nadalje, pa je
    /// nezavisno od toga koji je domen bio upisan. Redovi koji su već relativni
    /// se ne diraju (uslov LIKE 'http%').
    /// </summary>
    public partial class NormalizeMediaUrlsToRelative : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // CHARINDEX vraća 0 kada nema poklapanja — otuda i provera > 0,
            // da slučajan zapis bez "/uploads/" ne bude iseckan.
            migrationBuilder.Sql("""
                UPDATE [ListingImages]
                SET [ImageUrl] = SUBSTRING([ImageUrl], CHARINDEX('/uploads/', [ImageUrl]), LEN([ImageUrl]))
                WHERE [ImageUrl] LIKE 'http%' AND CHARINDEX('/uploads/', [ImageUrl]) > 0;
                """);

            migrationBuilder.Sql("""
                UPDATE [AspNetUsers]
                SET [ProfileImageUrl] = SUBSTRING([ProfileImageUrl], CHARINDEX('/uploads/', [ProfileImageUrl]), LEN([ProfileImageUrl]))
                WHERE [ProfileImageUrl] LIKE 'http%' AND CHARINDEX('/uploads/', [ProfileImageUrl]) > 0;
                """);

            migrationBuilder.Sql("""
                UPDATE [ProviderProfiles]
                SET [CoverImageUrl] = SUBSTRING([CoverImageUrl], CHARINDEX('/uploads/', [CoverImageUrl]), LEN([CoverImageUrl]))
                WHERE [CoverImageUrl] LIKE 'http%' AND CHARINDEX('/uploads/', [CoverImageUrl]) > 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Namerno prazno: povratak bi zahtevao da znamo koji je domen bio
            // upisan u svakom redu, a ta informacija je nepovratno izgubljena.
            // Aplikacija ionako podnosi oba oblika (MediaUrls.ToAbsolute
            // prosleđuje pun URL netaknut), pa unazadna kompatibilnost postoji.
        }
    }
}
