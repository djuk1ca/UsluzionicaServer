using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UsluzionicaServer.Migrations
{
    /// <inheritdoc />
    public partial class OnlineIGradoviOglasa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsOnline",
                table: "Listings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ListingCities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ListingId = table.Column<int>(type: "int", nullable: false),
                    City = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SearchCity = table.Column<string>(type: "varchar(420)", unicode: false, maxLength: 420, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ListingCities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ListingCities_Listings_ListingId",
                        column: x => x.ListingId,
                        principalTable: "Listings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ListingCities_ListingId",
                table: "ListingCities",
                column: "ListingId");

            migrationBuilder.CreateIndex(
                name: "IX_ListingCities_SearchCity",
                table: "ListingCities",
                column: "SearchCity")
                .Annotation("SqlServer:Include", new[] { "ListingId" });

            // Postojeći grad oglasa postaje njegov prvi (i jedini) grad.
            //
            // SearchCity se prepisuje iz SearchLocation: do ove migracije nijedan
            // oglas nije online, pa je SearchLocation tačno Fold(Location) — isto
            // što bi SearchIndexer upisao. Oglasi bez grada (ne bi trebalo da ih
            // ima) ostaju bez reda; ListingService.CitiesOf ih i dalje prikazuje
            // preko Location.
            migrationBuilder.Sql("""
                INSERT INTO ListingCities (ListingId, City, SearchCity, SortOrder)
                SELECT Id, LTRIM(RTRIM(Location)), SearchLocation, 0
                FROM Listings
                WHERE LTRIM(RTRIM(Location)) <> '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ListingCities");

            migrationBuilder.DropColumn(
                name: "IsOnline",
                table: "Listings");
        }
    }
}
