using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UsluzionicaServer.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchIndexColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Listings_Status_IsBoosted",
                table: "Listings");

            migrationBuilder.AddColumn<string>(
                name: "SearchBody",
                table: "Listings",
                type: "varchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SearchLocation",
                table: "Listings",
                type: "varchar(420)",
                unicode: false,
                maxLength: 420,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SearchTitle",
                table: "Listings",
                type: "varchar(700)",
                unicode: false,
                maxLength: 700,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "SearchVersion",
                table: "Listings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SearchName",
                table: "AspNetUsers",
                type: "varchar(400)",
                unicode: false,
                maxLength: 400,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "SearchVersion",
                table: "AspNetUsers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Listings_Search_Active",
                table: "Listings",
                columns: new[] { "Status", "IsBoosted", "BoostScore", "CreatedAt" },
                descending: new[] { false, true, true, true })
                .Annotation("SqlServer:Include", new[] { "SearchTitle", "SearchLocation", "CategoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_Listings_SearchLocation",
                table: "Listings",
                columns: new[] { "SearchLocation", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_SearchName",
                table: "AspNetUsers",
                column: "SearchName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Listings_Search_Active",
                table: "Listings");

            migrationBuilder.DropIndex(
                name: "IX_Listings_SearchLocation",
                table: "Listings");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_SearchName",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "SearchBody",
                table: "Listings");

            migrationBuilder.DropColumn(
                name: "SearchLocation",
                table: "Listings");

            migrationBuilder.DropColumn(
                name: "SearchTitle",
                table: "Listings");

            migrationBuilder.DropColumn(
                name: "SearchVersion",
                table: "Listings");

            migrationBuilder.DropColumn(
                name: "SearchName",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "SearchVersion",
                table: "AspNetUsers");

            migrationBuilder.CreateIndex(
                name: "IX_Listings_Status_IsBoosted",
                table: "Listings",
                columns: new[] { "Status", "IsBoosted" });
        }
    }
}
