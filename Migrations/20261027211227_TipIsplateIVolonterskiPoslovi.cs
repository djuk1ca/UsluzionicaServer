using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UsluzionicaServer.Migrations
{
    /// <inheritdoc />
    public partial class TipIsplateIVolonterskiPoslovi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsVolunteer",
                table: "Jobs",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PayType",
                table: "Jobs",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "OneTime");

            // Postojeći poslovi su jednokratni, osim onih bez iznosa: oni su
            // bili „po dogovoru" i to ostaju (Negotiable ⇔ PayAmount IS NULL).
            migrationBuilder.Sql("UPDATE Jobs SET PayType = 'Negotiable' WHERE PayAmount IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsVolunteer",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "PayType",
                table: "Jobs");
        }
    }
}
