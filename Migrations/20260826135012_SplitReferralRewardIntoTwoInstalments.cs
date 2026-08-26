using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UsluzionicaServer.Migrations
{
    /// <summary>
    /// Referral nagrada se deli na dve rate: 2 tokena na potvrdu emaila
    /// pozvanog, 3 tokena na aktivaciju njegovog provajder naloga.
    ///
    /// RUČNO ISPRAVLJENO nakon `dotnet ef migrations add`.
    ///
    /// EF je sam prepoznao preimenovanje kolona, ali je pogodio POGREŠAN SMER:
    /// predložio je TokensAwarded → SignupTokensAwarded. Stara kolona je, međutim,
    /// čuvala nagradu za AKTIVACIJU PROVAJDERA — to je jedini trenutak u kom se
    /// pre ove izmene išta isplaćivalo.
    ///
    /// Da je ostalo kako je EF predložio, zbir bi bio tačan (TotalTokensAwarded
    /// sabira obe kolone), ali bi svaki postojeći red tvrdio da je korisnik dobio
    /// 5 tokena za potvrdu emaila i 0 za aktivaciju — tačan novac, netačna priča.
    /// Prva reklamacija koju bi neko poslao ne bi se mogla razrešiti iz podataka.
    /// </summary>
    public partial class SplitReferralRewardIntoTwoInstalments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Stara kolona = nagrada za aktivaciju provajdera. Preimenovanje
            // (a ne drop + add) čuva vrednosti u postojećim redovima.
            migrationBuilder.RenameColumn(
                name: "TokensAwarded",
                table: "Referrals",
                newName: "ActivationTokensAwarded");

            migrationBuilder.RenameColumn(
                name: "RewardedAt",
                table: "Referrals",
                newName: "ActivationRewardedAt");

            // Nove kolone za prvu ratu. NULL u postojećim redovima je ispravno
            // stanje: ti pozivaoci nikad nisu dobili nagradu za potvrdu emaila
            // jer to pravilo tada nije postojalo.
            //
            // Namerno se NE radi retroaktivna isplata: svaka promena balansa
            // mora imati red u TokenTransactions, a šema migracija nije mesto
            // sa kog se piše u ledger. Postojeći Pending referrali i dalje
            // dobijaju drugu ratu — ReferralService.TryRewardActivationAsync
            // prihvata i status Pending, ne samo Registered.
            migrationBuilder.AddColumn<decimal>(
                name: "SignupTokensAwarded",
                table: "Referrals",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SignupRewardedAt",
                table: "Referrals",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SignupTokensAwarded",
                table: "Referrals");

            migrationBuilder.DropColumn(
                name: "SignupRewardedAt",
                table: "Referrals");

            migrationBuilder.RenameColumn(
                name: "ActivationTokensAwarded",
                table: "Referrals",
                newName: "TokensAwarded");

            migrationBuilder.RenameColumn(
                name: "ActivationRewardedAt",
                table: "Referrals",
                newName: "RewardedAt");
        }
    }
}
