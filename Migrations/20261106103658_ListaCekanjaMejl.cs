using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UsluzionicaServer.Migrations
{
    /// <inheritdoc />
    public partial class ListaCekanjaMejl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WaitlistEmails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Campaign = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    EmailHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ReservedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WaitlistEmails", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WaitlistOptOuts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmailHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OptedOutAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WaitlistOptOuts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WaitlistEmails_Campaign_EmailHash",
                table: "WaitlistEmails",
                columns: new[] { "Campaign", "EmailHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WaitlistOptOuts_EmailHash",
                table: "WaitlistOptOuts",
                column: "EmailHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WaitlistEmails");

            migrationBuilder.DropTable(
                name: "WaitlistOptOuts");
        }
    }
}
