using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UsluzionicaServer.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTokenAwardedFromServiceExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TokenAwarded",
                table: "ServiceExecutions");

            migrationBuilder.DropColumn(
                name: "TokenAwardedAt",
                table: "ServiceExecutions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TokenAwarded",
                table: "ServiceExecutions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "TokenAwardedAt",
                table: "ServiceExecutions",
                type: "datetime2",
                nullable: true);
        }
    }
}
