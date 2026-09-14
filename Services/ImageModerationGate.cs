using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.Infrastructure.Media;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Services;

/// <summary>
/// Spaja automatsku proveru slike sa redom za moderaciju.
///
/// Postoji kao zaseban servis, a ne kao poziv u svakom od tri upload puta,
/// zato što srednji ishod (<see cref="ImageVerdict.Review"/>) nije samo
/// „propusti" — mora i da napravi prijavu. Da je ta logika prepisana na tri
/// mesta, jedno bi pre ili kasnije propustilo da je napravi i slika bi prošla
/// bez ikakvog traga.
///
/// ZAŠTO NIJE U <see cref="ImageUploads"/>:
/// Ta klasa je statička, čista, bez DI i pokrivena unit testovima — proverava
/// bajtove i ne dodiruje ni bazu ni mrežu. Mrežni poziv u njoj pokvario bi sve
/// četiri osobine.
/// </summary>
public sealed class ImageModerationGate(
    AppDbContext                   db,
    IImageModerator                moderator,
    UserManager<ApplicationUser>   userManager,
    ILogger<ImageModerationGate>   logger)
{
    /// <summary>
    /// Proverava sliku i, ako treba, pravi prijavu.
    /// </summary>
    /// <param name="listingId">
    /// Popunjeno za slike oglasa. Za avatar i cover je null — tada se prijavljuje
    /// korisnik, jer oglas nije ni u igri.
    /// </param>
    /// <returns>
    /// <c>Error</c> različit od null znači da upload treba odbiti.
    /// </returns>
    public async Task<(bool Ok, string? Error)> ProveriAsync(
        IFormFile file, string vlasnikId, int? listingId, CancellationToken ct = default)
    {
        await using var stream = file.OpenReadStream();

        ImageVerdict presuda;
        string?      detalji;

        try
        {
            (presuda, detalji) = await moderator.ScanAsync(stream, ct);
        }
        catch (Exception ex)
        {
            // FAIL-OPEN NA DRUGOM NIVOU.
            //
            // CloudVisionImageModerator već hvata svoje greške, ali taj catch
            // štiti samo tu jednu implementaciju. Ovde se štiti UGOVOR: bilo
            // koji IImageModerator koji baci izuzetak — današnji ili sutrašnji,
            // sa drugim provajderom — ne sme da obori otpremanje slike.
            //
            // Bez ovoga bi pad provere značio da niko na celoj aplikaciji ne
            // može da postavi oglas. To je gori ishod od sumnjive slike koja je
            // prošla i čeka ručni pregled.
            logger.LogError(ex,
                "Provera slike je bacila izuzetak — slika ide na ručni pregled. " +
                "Korisnik {UserId}, oglas {ListingId}.", vlasnikId, listingId);

            presuda = ImageVerdict.Review;
            detalji = $"Provera nije uspela: {ex.GetType().Name}";
        }

        switch (presuda)
        {
            case ImageVerdict.Allow:
                return (true, null);

            case ImageVerdict.Reject:
                logger.LogWarning(
                    "Slika odbijena automatskom proverom. Korisnik {UserId}, oglas {ListingId}. {Detalji}",
                    vlasnikId, listingId, detalji);

                // Poruka namerno ne pominje „nudity" ni rezultat provere.
                // Automatska provera greši, pa optužujuća poruka pogađa i one
                // koji nisu ništa prekršili.
                return (false, "Slika nije prihvaćena. Izaberite drugu fotografiju.");

            case ImageVerdict.Review:
            default:
                await NapraviAutomatskuPrijavuAsync(vlasnikId, listingId, detalji, ct);
                return (true, null);
        }
    }

    /// <summary>
    /// Prijava koju podnosi sistem, ne korisnik.
    ///
    /// Prijavilac je admin nalog — <c>Report.ReporterId</c> je strani ključ na
    /// korisnike i mora pokazivati na postojeći red. Admin nalog se seeduje pri
    /// startu (<c>Program.SeedRolesAndAdminAsync</c>), pa uvek postoji.
    /// </summary>
    private async Task NapraviAutomatskuPrijavuAsync(
        string vlasnikId, int? listingId, string? detalji, CancellationToken ct)
    {
        var adminId = await PronadjiAdminaAsync();

        if (adminId is null)
        {
            // Bez admina nema ko da podnese prijavu. Upload i dalje prolazi —
            // fail-open je pravilo — ali ovo mora u logove, jer znači da je
            // sumnjiva slika prošla bez ijednog traga.
            logger.LogError(
                "Sumnjiva slika nije prijavljena: nema admin naloga. " +
                "Korisnik {UserId}, oglas {ListingId}. {Detalji}",
                vlasnikId, listingId, detalji);
            return;
        }

        // Isti admin ne sme dvaput da prijavi istu metu dok prva prijava čeka —
        // to čuva filtrirani UNIQUE indeks. Kod otpremanja pet slika na isti
        // oglas to bi značilo pet pokušaja i četiri izuzetka, pa se proverava
        // unapred.
        var vecPrijavljeno = await db.Reports.AnyAsync(r =>
            r.Status     == ReportStatus.Pending &&
            r.ReporterId == adminId &&
            (listingId != null ? r.ListingId == listingId : r.ReportedUserId == vlasnikId),
            ct);

        if (vecPrijavljeno) return;

        db.Reports.Add(new Report
        {
            ReporterId     = adminId,
            TargetType     = listingId is not null ? ReportTargetType.Listing : ReportTargetType.User,
            ListingId      = listingId,
            ReportedUserId = listingId is not null ? null : vlasnikId,
            Reason         = ReportReason.Neprikladno,
            Note           = $"Automatska provera slike: {detalji ?? "bez detalja"}",
            Status         = ReportStatus.Pending,
            CreatedAt      = DateTime.UtcNow
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Dva istovremena otpremanja su prošla proveru iznad. Prijava ipak
            // postoji, što je jedino što je bilo važno.
        }

        logger.LogInformation(
            "Sumnjiva slika automatski prijavljena. Korisnik {UserId}, oglas {ListingId}. {Detalji}",
            vlasnikId, listingId, detalji);
    }

    private async Task<string?> PronadjiAdminaAsync()
    {
        var admini = await userManager.GetUsersInRoleAsync("Admin");
        return admini.FirstOrDefault()?.Id;
    }
}
