using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UsluzionicaServer.Migrations
{
    /// <inheritdoc />
    public partial class ListaCekanjaBonus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WaitlistBonuses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmailHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    SignedUpAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AwardedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WaitlistBonuses", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WaitlistBonuses_EmailHash",
                table: "WaitlistBonuses",
                column: "EmailHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WaitlistBonuses_UserId",
                table: "WaitlistBonuses",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WaitlistBonuses");
        }
    }
}
