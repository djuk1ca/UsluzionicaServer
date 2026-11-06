using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UsluzionicaServer.Migrations
{
    /// <inheritdoc />
    public partial class SlikeUCetu : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MessageId",
                table: "Reports",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MessageAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MessageId = table.Column<int>(type: "int", nullable: false),
                    StoragePath = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Width = table.Column<int>(type: "int", nullable: false),
                    Height = table.Column<int>(type: "int", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    NeedsReview = table.Column<bool>(type: "bit", nullable: false),
                    HeldUntil = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessageAttachments_Messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "Messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Reports_MessageId",
                table: "Reports",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageAttachments_MessageId",
                table: "MessageAttachments",
                column: "MessageId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Reports_Messages_MessageId",
                table: "Reports",
                column: "MessageId",
                principalTable: "Messages",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Reports_Messages_MessageId",
                table: "Reports");

            migrationBuilder.DropTable(
                name: "MessageAttachments");

            migrationBuilder.DropIndex(
                name: "IX_Reports_MessageId",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "MessageId",
                table: "Reports");
        }
    }
}
