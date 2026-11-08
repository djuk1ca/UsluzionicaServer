using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UsluzionicaServer.Migrations
{
    /// <inheritdoc />
    public partial class SkinutoCekanjeIzvrsenja : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ExecuteAllowedAt",
                table: "BookingRequests",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExecuteAllowedById",
                table: "BookingRequests",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsTrusted",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExecuteAllowedAt",
                table: "BookingRequests");

            migrationBuilder.DropColumn(
                name: "ExecuteAllowedById",
                table: "BookingRequests");

            migrationBuilder.DropColumn(
                name: "IsTrusted",
                table: "AspNetUsers");
        }
    }
}
