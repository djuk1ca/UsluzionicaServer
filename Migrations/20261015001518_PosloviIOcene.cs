using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UsluzionicaServer.Migrations
{
    /// <inheritdoc />
    public partial class PosloviIOcene : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Reviews_AuthorId",
                table: "Reviews");

            migrationBuilder.DropIndex(
                name: "IX_Reviews_BookingRequestId",
                table: "Reviews");

            migrationBuilder.DropIndex(
                name: "IX_Reviews_ListingId_AuthorId",
                table: "Reviews");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Report_JednaMeta",
                table: "Reports");

            migrationBuilder.AlterColumn<int>(
                name: "ListingId",
                table: "Reviews",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddColumn<string>(
                name: "ContextTitle",
                table: "Reviews",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsVerified",
                table: "Reviews",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "JobId",
                table: "Reviews",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PublishedAt",
                table: "Reviews",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RevealAt",
                table: "Reviews",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "SubjectRole",
                table: "Reviews",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SubjectUserId",
                table: "Reviews",
                type: "nvarchar(450)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "JobId",
                table: "Reports",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "JobApplicationId",
                table: "Messages",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "Messages",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Text");

            migrationBuilder.AddColumn<decimal>(
                name: "ClientRating",
                table: "AspNetUsers",
                type: "decimal(3,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "ClientReviewCount",
                table: "AspNetUsers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // Postojeće ocene su sve od klijenta ka uslugodavcu: popunjavaju se
            // smer i naslov, a vidljivost ostaje kakva je bila — potvrđene (uz
            // uslugu) se i dalje vide i broje, stare bez usluge i dalje ne.
            // Mora pre FK na SubjectUserId (prazan string nije korisnik).
            migrationBuilder.Sql("""
                UPDATE r SET
                    r.SubjectUserId = pp.UserId,
                    r.SubjectRole   = 'Provider',
                    r.ContextTitle  = LEFT(l.Title, 200),
                    r.IsVerified    = CASE WHEN r.BookingRequestId IS NULL THEN 0 ELSE 1 END,
                    r.PublishedAt   = r.CreatedAt,
                    r.RevealAt      = r.CreatedAt
                FROM Reviews r
                JOIN Listings l          ON l.Id  = r.ListingId
                JOIN ProviderProfiles pp ON pp.Id = l.ProviderProfileId;
                """);

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AuthorId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    CategoryId = table.Column<int>(type: "int", nullable: false),
                    Location = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    PayAmount = table.Column<decimal>(type: "decimal(10,2)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    AssignedApplicationId = table.Column<int>(type: "int", nullable: true),
                    AssignedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CancelCount = table.Column<int>(type: "int", nullable: false),
                    CompletableNotifiedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ApplicationsSeenAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RemainingOnAssign = table.Column<long>(type: "bigint", nullable: true),
                    ExpiryNotifiedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExpiredAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PurgeAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsBoosted = table.Column<bool>(type: "bit", nullable: false),
                    BoostExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    BoostScore = table.Column<decimal>(type: "decimal(8,2)", nullable: false),
                    BoostRemainingOnAssign = table.Column<long>(type: "bigint", nullable: true),
                    ModerationState = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SearchTitle = table.Column<string>(type: "varchar(700)", unicode: false, maxLength: 700, nullable: false),
                    SearchLocation = table.Column<string>(type: "varchar(420)", unicode: false, maxLength: 420, nullable: false),
                    SearchBody = table.Column<string>(type: "varchar(max)", nullable: false),
                    SearchVersion = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Jobs_AspNetUsers_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Jobs_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JobApplications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    JobId = table.Column<int>(type: "int", nullable: false),
                    ProviderUserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    OfferAmount = table.Column<decimal>(type: "decimal(10,2)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CancelReason = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    IsReapplied = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobApplications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobApplications_AspNetUsers_ProviderUserId",
                        column: x => x.ProviderUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JobApplications_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JobBoosts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    JobId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    TokensSpent = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    DurationDays = table.Column<int>(type: "int", nullable: false),
                    StartsAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobBoosts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobBoosts_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JobBoosts_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JobImages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    JobId = table.Column<int>(type: "int", nullable: false),
                    ImageUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobImages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobImages_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_AuthorId_BookingRequestId",
                table: "Reviews",
                columns: new[] { "AuthorId", "BookingRequestId" },
                unique: true,
                filter: "[BookingRequestId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_AuthorId_JobId",
                table: "Reviews",
                columns: new[] { "AuthorId", "JobId" },
                unique: true,
                filter: "[JobId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_BookingRequestId",
                table: "Reviews",
                column: "BookingRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_JobId",
                table: "Reviews",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_ListingId",
                table: "Reviews",
                column: "ListingId");

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_RevealAt",
                table: "Reviews",
                column: "RevealAt",
                filter: "[PublishedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_SubjectUserId_SubjectRole_PublishedAt",
                table: "Reviews",
                columns: new[] { "SubjectUserId", "SubjectRole", "PublishedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Reports_JobId",
                table: "Reports",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_Reports_ReporterId_JobId",
                table: "Reports",
                columns: new[] { "ReporterId", "JobId" },
                unique: true,
                filter: "[Status] = 'Pending' AND [JobId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Report_JednaMeta",
                table: "Reports",
                sql: "(CASE WHEN [ListingId] IS NULL THEN 0 ELSE 1 END) + (CASE WHEN [ReportedUserId] IS NULL THEN 0 ELSE 1 END) + (CASE WHEN [JobId] IS NULL THEN 0 ELSE 1 END) = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_JobApplicationId",
                table: "Messages",
                column: "JobApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_JobApplications_JobId_ProviderUserId",
                table: "JobApplications",
                columns: new[] { "JobId", "ProviderUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobApplications_ProviderUserId_Status",
                table: "JobApplications",
                columns: new[] { "ProviderUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_JobBoosts_JobId",
                table: "JobBoosts",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_JobBoosts_UserId",
                table: "JobBoosts",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_JobImages_JobId",
                table: "JobImages",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_AuthorId_Status",
                table: "Jobs",
                columns: new[] { "AuthorId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_CategoryId",
                table: "Jobs",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_Search_Open",
                table: "Jobs",
                columns: new[] { "Status", "IsBoosted", "BoostScore", "CreatedAt" },
                descending: new[] { false, true, true, true })
                .Annotation("SqlServer:Include", new[] { "SearchTitle", "SearchLocation", "CategoryId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_SearchLocation",
                table: "Jobs",
                columns: new[] { "SearchLocation", "Status" });

            migrationBuilder.AddForeignKey(
                name: "FK_Messages_JobApplications_JobApplicationId",
                table: "Messages",
                column: "JobApplicationId",
                principalTable: "JobApplications",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Reports_Jobs_JobId",
                table: "Reports",
                column: "JobId",
                principalTable: "Jobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Reviews_AspNetUsers_SubjectUserId",
                table: "Reviews",
                column: "SubjectUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Reviews_Jobs_JobId",
                table: "Reviews",
                column: "JobId",
                principalTable: "Jobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Messages_JobApplications_JobApplicationId",
                table: "Messages");

            migrationBuilder.DropForeignKey(
                name: "FK_Reports_Jobs_JobId",
                table: "Reports");

            migrationBuilder.DropForeignKey(
                name: "FK_Reviews_AspNetUsers_SubjectUserId",
                table: "Reviews");

            migrationBuilder.DropForeignKey(
                name: "FK_Reviews_Jobs_JobId",
                table: "Reviews");

            migrationBuilder.DropTable(
                name: "JobApplications");

            migrationBuilder.DropTable(
                name: "JobBoosts");

            migrationBuilder.DropTable(
                name: "JobImages");

            migrationBuilder.DropTable(
                name: "Jobs");

            migrationBuilder.DropIndex(
                name: "IX_Reviews_AuthorId_BookingRequestId",
                table: "Reviews");

            migrationBuilder.DropIndex(
                name: "IX_Reviews_AuthorId_JobId",
                table: "Reviews");

            migrationBuilder.DropIndex(
                name: "IX_Reviews_BookingRequestId",
                table: "Reviews");

            migrationBuilder.DropIndex(
                name: "IX_Reviews_JobId",
                table: "Reviews");

            migrationBuilder.DropIndex(
                name: "IX_Reviews_ListingId",
                table: "Reviews");

            migrationBuilder.DropIndex(
                name: "IX_Reviews_RevealAt",
                table: "Reviews");

            migrationBuilder.DropIndex(
                name: "IX_Reviews_SubjectUserId_SubjectRole_PublishedAt",
                table: "Reviews");

            migrationBuilder.DropIndex(
                name: "IX_Reports_JobId",
                table: "Reports");

            migrationBuilder.DropIndex(
                name: "IX_Reports_ReporterId_JobId",
                table: "Reports");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Report_JednaMeta",
                table: "Reports");

            migrationBuilder.DropIndex(
                name: "IX_Messages_JobApplicationId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ContextTitle",
                table: "Reviews");

            migrationBuilder.DropColumn(
                name: "IsVerified",
                table: "Reviews");

            migrationBuilder.DropColumn(
                name: "JobId",
                table: "Reviews");

            migrationBuilder.DropColumn(
                name: "PublishedAt",
                table: "Reviews");

            migrationBuilder.DropColumn(
                name: "RevealAt",
                table: "Reviews");

            migrationBuilder.DropColumn(
                name: "SubjectRole",
                table: "Reviews");

            migrationBuilder.DropColumn(
                name: "SubjectUserId",
                table: "Reviews");

            migrationBuilder.DropColumn(
                name: "JobId",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "JobApplicationId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ClientRating",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ClientReviewCount",
                table: "AspNetUsers");

            migrationBuilder.AlterColumn<int>(
                name: "ListingId",
                table: "Reviews",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_AuthorId",
                table: "Reviews",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_BookingRequestId",
                table: "Reviews",
                column: "BookingRequestId",
                unique: true,
                filter: "[BookingRequestId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_ListingId_AuthorId",
                table: "Reviews",
                columns: new[] { "ListingId", "AuthorId" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Report_JednaMeta",
                table: "Reports",
                sql: "(CASE WHEN [ListingId] IS NULL THEN 0 ELSE 1 END) + (CASE WHEN [ReportedUserId] IS NULL THEN 0 ELSE 1 END) = 1");
        }
    }
}
